package app.relaxkonos.mobile.security

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Raised when the Keystore refuses to create a key for the requested authentication policy.
 *
 * This is deliberately distinct from [VaultKeyInvalidatedException]. Nothing was invalidated and no
 * record has to be dropped, so a caller must degrade to "unlock unavailable" — typing the password
 * still works — instead of deleting what the user saved.
 */
class VaultKeyUnavailableException(message: String, cause: Throwable? = null) : Exception(message, cause)

/**
 * Owns the two AES-256-GCM Keystore keys behind the credential vaults.
 *
 * Key material is generated inside the Keystore and never leaves it; callers only ever receive a
 * [`Cipher`] that the platform has bound to a user-authentication requirement. See
 * `RelaxKonOS.Mobile.V1.Design.md` §5.4 for the parameter choices.
 */
class VaultKeyManager : VaultCrypto {
    override fun sealCipher(kind: VaultKind): Cipher = newCipher(kind, Cipher.ENCRYPT_MODE, null)

    override fun openCipher(kind: VaultKind, iv: ByteArray): Cipher = newCipher(kind, Cipher.DECRYPT_MODE, iv)

    /** Creates the key for [kind] when it is missing. Idempotent. */
    fun ensureKey(kind: VaultKind, mode: VaultUnlockMode) {
        if (hasKey(kind)) {
            VaultDiagnostics.trace("key.present", "${kind.name} alias already exists")
            return
        }
        VaultDiagnostics.trace("key.create", "${kind.name} mode=$mode")
        try {
            generate(kind, mode)
        } catch (error: Exception) {
            // Typically a weak-biometric-only device with no device credential, or a policy the
            // platform rejects outright. Any failure to produce the key means the vault reports
            // "unavailable" — it must never be reported as invalidated, because "we could not create
            // a key" and "your fingerprint changed" call for opposite advice to the user.
            VaultDiagnostics.failure("key.create.failed", error)
            throw VaultKeyUnavailableException("Unable to create the ${kind.name} vault key.", error)
        }
        // A key generator that accepts a policy but stores nothing would surface much later as "the
        // key is missing", which reads as a biometric change. Verifying here keeps the contradiction
        // at the point where it can still be described honestly.
        if (!hasKey(kind)) {
            VaultDiagnostics.trace("key.create.absent", "${kind.name} alias missing after generate()")
            throw VaultKeyUnavailableException("The ${kind.name} vault key was not created.")
        }
        VaultDiagnostics.trace("key.created", "${kind.name} alias created")
    }

    fun hasKey(kind: VaultKind): Boolean = keyStore().containsAlias(alias(kind))

    /**
     * Drops the key. Every record protected by it becomes undecryptable, which is the intended
     * outcome when the user turns the fingerprint master switch off.
     */
    fun deleteKey(kind: VaultKind) {
        runCatching { keyStore().deleteEntry(alias(kind)) }
    }

    private fun newCipher(kind: VaultKind, mode: Int, iv: ByteArray?): Cipher = try {
        val key = keyStore().getKey(alias(kind), null) as? SecretKey
        if (key == null) {
            // Either the key was never created, or the alias was wiped by an uninstall-like event.
            // Both are "we have no key", not "the user's fingerprint changed": log which one it is
            // before the vault collapses every missing-key case into one user-facing sentence.
            VaultDiagnostics.trace("key.missing", "${kind.name} alias holds no key to initialise a Cipher with")
            throw VaultKeyInvalidatedException("The ${kind.name} vault key is missing.")
        }
        Cipher.getInstance(TRANSFORMATION).apply {
            if (iv == null) {
                init(mode, key)
            } else {
                init(mode, key, GCMParameterSpec(TAG_LENGTH_BITS, iv))
            }
        }
    } catch (error: Throwable) {
        mapKeyException(kind, error)
    }

    private fun generate(kind: VaultKind, mode: VaultUnlockMode) {
        val builder = KeyGenParameterSpec.Builder(
            alias(kind),
            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
        )
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(KEY_SIZE_BITS)
            .setUserAuthenticationRequired(true)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            when (mode) {
                // Timeout 0 means "authorize every single use" and is only permitted for strong
                // biometrics. Weak biometrics and device credentials must use a window.
                VaultUnlockMode.PerUseStrongBiometric ->
                    builder.setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)

                // Keystore exposes no "weak biometric" flag: `setUserAuthenticationParameters`
                // accepts only AUTH_BIOMETRIC_STRONG and AUTH_DEVICE_CREDENTIAL. A time-bound key is
                // therefore released by a strong biometric or by the device credential, and on a
                // weak-biometric-only device only the device credential can release it — which is
                // exactly why D2/D3 label the connection vault as reduced strength.
                VaultUnlockMode.DeviceUnlockWindow ->
                    builder.setUserAuthenticationParameters(
                        DEVICE_UNLOCK_WINDOW_SECONDS,
                        KeyProperties.AUTH_DEVICE_CREDENTIAL or KeyProperties.AUTH_BIOMETRIC_STRONG,
                    )
            }
        } else {
            @Suppress("DEPRECATION")
            builder.setUserAuthenticationValidityDurationSeconds(
                if (mode == VaultUnlockMode.PerUseStrongBiometric) AUTH_PER_USE else DEVICE_UNLOCK_WINDOW_SECONDS,
            )
        }

        // A new biometric enrolment invalidates the key. The platform rejects this flag together
        // with a non-zero validity window, so window keys simply keep the platform default.
        if (mode == VaultUnlockMode.PerUseStrongBiometric) {
            runCatching { builder.setInvalidatedByBiometricEnrollment(true) }
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            runCatching { builder.setUnlockedDeviceRequired(true) }
        }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            try {
                generator.init(strongBoxSpec(builder).build())
                generator.generateKey()
                VaultDiagnostics.trace("key.provider", "${kind.name} strongbox")
                return
            } catch (_: StrongBoxUnavailableException) {
                // Fall back to the TEE: hardware-backed-less devices must not lose the feature.
                VaultDiagnostics.trace("key.provider", "${kind.name} strongbox unavailable, retrying on TEE")
            } catch (_: IllegalArgumentException) {
                // Some vendors advertise StrongBox but reject the combination; retry without it.
                VaultDiagnostics.trace("key.provider", "${kind.name} strongbox spec rejected, retrying on TEE")
            }
        }
        generator.init(builder.build())
        // The alias only exists once the generator has been asked to produce the key: configuring a
        // policy is not creating a key. Skipping this call made every later lookup of the alias
        // return null, which the vault reported to the user as a changed fingerprint.
        generator.generateKey()
        VaultDiagnostics.trace("key.provider", "${kind.name} tee")
    }

    private fun strongBoxSpec(builder: KeyGenParameterSpec.Builder): KeyGenParameterSpec.Builder = builder
        .setIsStrongBoxBacked(true)

    private fun keyStore(): KeyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

    private fun alias(kind: VaultKind): String = when (kind) {
        VaultKind.Connection -> "rk.connection.vault"
        VaultKind.Elevation -> "rk.elevation.vault"
    }

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val TAG_LENGTH_BITS = 128
        const val KEY_SIZE_BITS = 256
        const val AUTH_PER_USE = -1

        /** D3: device-credential unlocking is valid for five minutes after the last device unlock. */
        const val DEVICE_UNLOCK_WINDOW_SECONDS = 300
    }
}
