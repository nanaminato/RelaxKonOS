package app.relaxkonos.mobile.security

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import java.security.GeneralSecurityException
import java.security.KeyStore
import java.security.UnrecoverableKeyException
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
            return
        }
        try {
            generate(kind, mode)
        } catch (error: GeneralSecurityException) {
            // Typically a weak-biometric-only device with no device credential: the platform cannot
            // satisfy the policy at all, so the vault reports "unavailable" rather than "tampered".
            throw VaultKeyUnavailableException("Unable to create the ${kind.name} vault key.", error)
        } catch (error: IllegalArgumentException) {
            throw VaultKeyUnavailableException("The ${kind.name} vault key policy was rejected.", error)
        }
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
            ?: throw VaultKeyInvalidatedException("The ${kind.name} vault key is missing.")
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
                return
            } catch (_: StrongBoxUnavailableException) {
                // Fall back to the TEE: hardware-backed-less devices must not lose the feature.
            } catch (_: IllegalArgumentException) {
                // Some vendors advertise StrongBox but reject the combination; retry without it.
            }
        }
        generator.init(builder.build())
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
