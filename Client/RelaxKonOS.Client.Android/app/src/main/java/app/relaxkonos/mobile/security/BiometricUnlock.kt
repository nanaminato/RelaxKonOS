package app.relaxkonos.mobile.security

import android.os.Handler
import android.os.Looper
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.fragment.app.FragmentActivity
import java.util.concurrent.Executor
import javax.crypto.Cipher
import kotlin.coroutines.resume
import kotlinx.coroutines.suspendCancellableCoroutine

/** Why unlocking did not complete. Mapped to localised text by the UI, never shown raw. */
enum class UnlockFailure {
    /** Too many failed attempts; retry later or type the password. */
    LockedOut,

    /** The authenticator is locked until the device is unlocked with its credential. */
    LockedOutPermanently,

    /** The Keystore key was invalidated (new biometric enrolment, or credentials reset). */
    KeyInvalidated,

    /** No usable authenticator right now, or a hardware/processing error. */
    Unavailable,

    /** The stored payload failed authentication; the record is unusable. */
    Tampered,

    Unknown,
}

/** Result of one biometric authorization. */
sealed interface UnlockOutcome {
    /**
     * Authorized. [cipher] carries the CryptoObject-bound Cipher for per-use mode and is `null`
     * when the authorization came from a device-unlock window.
     */
    data class Authorized(val cipher: Cipher?) : UnlockOutcome

    /** The user dismissed the prompt. Records are untouched (design §5.4). */
    data object Cancelled : UnlockOutcome

    data class Rejected(val failure: UnlockFailure) : UnlockOutcome
}

/**
 * Thin coroutine wrapper over `BiometricPrompt`.
 *
 * Two shapes are used, matching the two key types:
 * - per-use: the prompt receives a `CryptoObject`, and only the Cipher returned by the platform can
 *   decrypt the record;
 * - device window: a plain prompt authorizes the user, after which the Keystore key is released for
 *   a five-minute window (D3, connection vault only).
 *
 * The authenticators offered by each prompt are exactly the ones the matching Keystore key accepts.
 * They must stay in step: a prompt that succeeded with an authenticator the key does not accept would
 * report a successful unlock and then fail to decrypt, which the vault has to treat as tampering.
 */
class BiometricUnlock {
    suspend fun authorize(
        activity: FragmentActivity,
        mode: VaultUnlockMode,
        cipher: Cipher?,
        title: String,
        subtitle: String,
        negativeButton: String,
    ): UnlockOutcome {
        val builder = BiometricPrompt.PromptInfo.Builder()
            .setTitle(title)
            .setSubtitle(subtitle)
            .setConfirmationRequired(false)

        when (mode) {
            VaultUnlockMode.PerUseStrongBiometric -> builder
                .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG)
                .setNegativeButtonText(negativeButton)

            // Mirrors the window key, which is released by the device credential or a strong
            // biometric. Device credential cannot be combined with a negative button: the platform
            // supplies its own cancel affordance for that combination.
            VaultUnlockMode.DeviceUnlockWindow -> builder
                .setAllowedAuthenticators(
                    BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.DEVICE_CREDENTIAL,
                )
        }

        val promptInfo = builder.build()
        return suspendCancellableCoroutine { continuation ->
            val prompt = BiometricPrompt(
                activity,
                mainExecutor(),
                object : BiometricPrompt.AuthenticationCallback() {
                    override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                        if (continuation.isActive) {
                            continuation.resume(UnlockOutcome.Authorized(result.cryptoObject?.cipher))
                        }
                    }

                    override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                        if (!continuation.isActive) {
                            return
                        }
                        val outcome = if (errorCode.isDismissal()) {
                            UnlockOutcome.Cancelled
                        } else {
                            UnlockOutcome.Rejected(errorCode.toFailure())
                        }
                        continuation.resume(outcome)
                    }

                    override fun onAuthenticationFailed() {
                        // One non-matching sample is not fatal: the prompt stays open.
                    }
                },
            )
            continuation.invokeOnCancellation { runCatching { prompt.cancelAuthentication() } }
            if (cipher == null) {
                prompt.authenticate(promptInfo)
            } else {
                prompt.authenticate(promptInfo, BiometricPrompt.CryptoObject(cipher))
            }
        }
    }

    private fun mainExecutor(): Executor = Executor { command -> Handler(Looper.getMainLooper()).post(command) }

    private fun Int.isDismissal(): Boolean =
        this == BiometricPrompt.ERROR_USER_CANCELED || this == BiometricPrompt.ERROR_NEGATIVE_BUTTON

    private fun Int.toFailure(): UnlockFailure = when (this) {
        BiometricPrompt.ERROR_LOCKOUT -> UnlockFailure.LockedOut
        BiometricPrompt.ERROR_LOCKOUT_PERMANENT -> UnlockFailure.LockedOutPermanently
        BiometricPrompt.ERROR_HW_NOT_PRESENT,
        BiometricPrompt.ERROR_HW_UNAVAILABLE,
        BiometricPrompt.ERROR_NO_BIOMETRICS,
        BiometricPrompt.ERROR_NO_DEVICE_CREDENTIAL,
        -> UnlockFailure.Unavailable

        else -> UnlockFailure.Unknown
    }
}

/** Outcome of a vault operation that includes a biometric step. */
sealed interface VaultOperation<out T> {
    data class Success<T>(val value: T) : VaultOperation<T>
    data object Cancelled : VaultOperation<Nothing>
    data class Failed(val failure: UnlockFailure) : VaultOperation<Nothing>
}

/**
 * Runs the complete save/load sequence for one credential, hiding the difference between per-use and
 * device-window keys from the screens.
 */
class VaultAccess(
    private val vault: CredentialVault,
    private val keys: VaultKeyManager,
    private val lock: BiometricUnlock,
) {
    /**
     * Saves [password] after a successful biometric confirmation. The caller must have obtained the
     * password from an explicit user action; nothing is ever stored silently (design §5.8.1).
     */
    suspend fun save(
        kind: VaultKind,
        mode: VaultUnlockMode,
        serverUrl: String,
        account: String,
        password: CharArray,
        activity: FragmentActivity,
        title: String,
        subtitle: String,
        negativeButton: String,
        nowEpochMillis: Long,
    ): VaultOperation<VaultRecord> = try {
        val cipher = when (mode) {
            VaultUnlockMode.PerUseStrongBiometric -> {
                keys.ensureKey(kind, mode)
                val pending = vault.beginSeal(kind)
                when (val outcome = lock.authorize(activity, mode, pending, title, subtitle, negativeButton)) {
                    is UnlockOutcome.Authorized ->
                        outcome.cipher ?: return VaultOperation.Failed(UnlockFailure.Unknown)

                    UnlockOutcome.Cancelled -> return VaultOperation.Cancelled
                    is UnlockOutcome.Rejected -> return VaultOperation.Failed(outcome.failure)
                }
            }

            VaultUnlockMode.DeviceUnlockWindow -> {
                keys.ensureKey(kind, mode)
                when (val outcome = lock.authorize(activity, mode, null, title, subtitle, negativeButton)) {
                    // The key is only released after the window authorization, so it is created here.
                    is UnlockOutcome.Authorized -> vault.beginSeal(kind)
                    UnlockOutcome.Cancelled -> return VaultOperation.Cancelled
                    is UnlockOutcome.Rejected -> return VaultOperation.Failed(outcome.failure)
                }
            }
        }

        VaultOperation.Success(
            vault.seal(
                kind = kind,
                serverUrl = serverUrl,
                account = account,
                password = password,
                cipher = cipher,
                fingerprintProtected = true,
                nowEpochMillis = nowEpochMillis,
            ),
        )
    } catch (_: VaultKeyInvalidatedException) {
        VaultOperation.Failed(UnlockFailure.KeyInvalidated)
    } catch (_: VaultKeyUnavailableException) {
        // The device cannot satisfy the key policy at all. Nothing was corrupted and nothing needs to
        // be deleted, so the caller degrades to "type the password".
        VaultOperation.Failed(UnlockFailure.Unavailable)
    } catch (_: VaultTamperException) {
        VaultOperation.Failed(UnlockFailure.Tampered)
    }

    /** Decrypts [record] after a successful biometric confirmation. */
    suspend fun load(
        mode: VaultUnlockMode,
        record: VaultRecord,
        activity: FragmentActivity,
        title: String,
        subtitle: String,
        negativeButton: String,
    ): VaultOperation<CharArray> = try {
        val cipher = when (mode) {
            VaultUnlockMode.PerUseStrongBiometric -> {
                val pending = vault.beginOpen(record)
                when (val outcome = lock.authorize(activity, mode, pending, title, subtitle, negativeButton)) {
                    is UnlockOutcome.Authorized ->
                        outcome.cipher ?: return VaultOperation.Failed(UnlockFailure.Unknown)

                    UnlockOutcome.Cancelled -> return VaultOperation.Cancelled
                    is UnlockOutcome.Rejected -> return VaultOperation.Failed(outcome.failure)
                }
            }

            VaultUnlockMode.DeviceUnlockWindow -> {
                when (val outcome = lock.authorize(activity, mode, null, title, subtitle, negativeButton)) {
                    is UnlockOutcome.Authorized -> vault.beginOpen(record)
                    UnlockOutcome.Cancelled -> return VaultOperation.Cancelled
                    is UnlockOutcome.Rejected -> return VaultOperation.Failed(outcome.failure)
                }
            }
        }

        VaultOperation.Success(vault.open(record, cipher))
    } catch (_: VaultKeyInvalidatedException) {
        VaultOperation.Failed(UnlockFailure.KeyInvalidated)
    } catch (_: VaultKeyUnavailableException) {
        // The stored record is untouched: an unavailable authenticator is not evidence of tampering.
        VaultOperation.Failed(UnlockFailure.Unavailable)
    } catch (_: VaultTamperException) {
        VaultOperation.Failed(UnlockFailure.Tampered)
    }
}
