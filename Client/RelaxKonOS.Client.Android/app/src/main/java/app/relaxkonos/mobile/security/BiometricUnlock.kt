package app.relaxkonos.mobile.security

import android.os.Handler
import android.os.Looper
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.fragment.app.FragmentActivity
import java.util.concurrent.Executor
import javax.crypto.Cipher
import kotlin.coroutines.resume
import kotlinx.coroutines.CancellationException
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
 *
 * An interface rather than a concrete class so [VaultAccess]'s handling of a *refused* prompt — the
 * case where the platform throws instead of answering — can be verified in a JVM test. That path is
 * what used to leave a sign-in form permanently busy on a real device.
 */
interface BiometricUnlock {
    suspend fun authorize(
        activity: FragmentActivity,
        mode: VaultUnlockMode,
        cipher: Cipher?,
        title: String,
        subtitle: String,
        negativeButton: String,
    ): UnlockOutcome
}

/** [BiometricUnlock] backed by `androidx.biometric`. */
class AndroidBiometricUnlock : BiometricUnlock {
    override suspend fun authorize(
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
        VaultDiagnostics.trace("prompt.show", "mode=$mode cryptoObject=${cipher != null}")
        return suspendCancellableCoroutine { continuation ->
            val prompt = BiometricPrompt(
                activity,
                mainExecutor(),
                object : BiometricPrompt.AuthenticationCallback() {
                    override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                        VaultDiagnostics.trace(
                            "prompt.succeeded",
                            "mode=$mode cipherReturned=${result.cryptoObject?.cipher != null}",
                        )
                        if (continuation.isActive) {
                            continuation.resume(UnlockOutcome.Authorized(result.cryptoObject?.cipher))
                        }
                    }

                    override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                        // Logged before the liveness check: an error that arrived after the caller went
                        // away is still the platform's verdict on this attempt.
                        VaultDiagnostics.trace("prompt.error", "code=$errorCode message=$errString")
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
                        VaultDiagnostics.trace("prompt.retry", "one sample not recognised")
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
 * The verdict for a refusal the vault has no name for.
 *
 * The platform can refuse in ways this client never enumerated: a Keystore provider that throws a
 * runtime exception, a prompt the system declines to show at all, a `Cipher` factory that fails for a
 * reason outside `GeneralSecurityException`. Letting such an exception escape lands in a UI coroutine
 * that has no idea what to do with it — which is how a sign-in form ended up with its busy flag stuck
 * on, every field and the button disabled until the process died.
 *
 * [UnlockFailure.Unknown] is the verdict that is always safe: it never deletes a record, never claims a
 * key is dead, and always falls back to "type the password". Cancellation is not routed here — see the
 * call sites, which rethrow it ahead of this.
 */
internal fun unnameableVaultFailure(operation: String, error: Exception): VaultOperation.Failed {
    VaultDiagnostics.failure("vault.$operation.unexpected", error)
    return VaultOperation.Failed(UnlockFailure.Unknown)
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
        serviceId: String,
        account: String,
        password: CharArray,
        activity: FragmentActivity,
        title: String,
        subtitle: String,
        negativeButton: String,
        nowEpochMillis: Long,
    ): VaultOperation<VaultRecord> = saveOnce(
        kind = kind,
        mode = mode,
        serviceId = serviceId,
        account = account,
        password = password,
        activity = activity,
        title = title,
        subtitle = subtitle,
        negativeButton = negativeButton,
        nowEpochMillis = nowEpochMillis,
        canReplaceInvalidatedKey = true,
    )

    /**
     * Performs one explicit save request, repairing a permanently invalid Keystore alias at most once.
     *
     * The alias is shared by every record of [kind]. A biometric enrolment change makes that alias
     * unusable forever; merely reporting the failure leaves the user unable to save a replacement on
     * every later login. The current password was both server-verified and explicitly offered for
     * saving, so it is safe to replace the dead alias, request a fresh authorization, and seal this
     * one identity. The other records stay present but are marked invalidated because the old alias
     * could not possibly open them.
     */
    private suspend fun saveOnce(
        kind: VaultKind,
        mode: VaultUnlockMode,
        serviceId: String,
        account: String,
        password: CharArray,
        activity: FragmentActivity,
        title: String,
        subtitle: String,
        negativeButton: String,
        nowEpochMillis: Long,
        canReplaceInvalidatedKey: Boolean,
    ): VaultOperation<VaultRecord> = try {
        VaultDiagnostics.trace(
            "vault.save.begin",
            "kind=${kind.name} mode=$mode mayRotateAlias=$canReplaceInvalidatedKey",
        )
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

        VaultDiagnostics.trace("vault.seal", "kind=${kind.name} authorized Cipher from mode=$mode")
        VaultOperation.Success(
            vault.seal(
                kind = kind,
                serviceId = serviceId,
                account = account,
                password = password,
                cipher = cipher,
                fingerprintProtected = true,
                nowEpochMillis = nowEpochMillis,
            ),
        )
    } catch (error: VaultKeyInvalidatedException) {
        if (!canReplaceInvalidatedKey) {
            VaultDiagnostics.failure("vault.save.invalidated", error)
            VaultOperation.Failed(UnlockFailure.KeyInvalidated)
        } else {
            VaultDiagnostics.trace("vault.save.rotate", "${kind.name} alias unusable, replacing it")
            vault.markAllInvalidated(kind)
            keys.deleteKey(kind)
            saveOnce(
                kind = kind,
                mode = mode,
                serviceId = serviceId,
                account = account,
                password = password,
                activity = activity,
                title = title,
                subtitle = subtitle,
                negativeButton = negativeButton,
                nowEpochMillis = nowEpochMillis,
                canReplaceInvalidatedKey = false,
            )
        }
    } catch (error: VaultKeyUnavailableException) {
        // The device cannot satisfy the key policy at all. Nothing was corrupted and nothing needs to
        // be deleted, so the caller degrades to "type the password".
        VaultDiagnostics.failure("vault.save.unavailable", error)
        VaultOperation.Failed(UnlockFailure.Unavailable)
    } catch (error: VaultTamperException) {
        VaultDiagnostics.failure("vault.save.tampered", error)
        VaultOperation.Failed(UnlockFailure.Tampered)
    } catch (error: CancellationException) {
        // The caller went away; that is not a verdict on the vault.
        throw error
    } catch (error: Exception) {
        unnameableVaultFailure(operation = "save", error = error)
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
        VaultDiagnostics.trace("vault.load.begin", "kind=${record.kind.name} mode=$mode")
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

        VaultDiagnostics.trace("vault.open", "kind=${record.kind.name} record unsealed")
        VaultOperation.Success(vault.open(record, cipher))
    } catch (error: VaultKeyInvalidatedException) {
        VaultDiagnostics.failure("vault.load.invalidated", error)
        VaultOperation.Failed(UnlockFailure.KeyInvalidated)
    } catch (error: VaultRecordInvalidatedException) {
        VaultDiagnostics.failure("vault.load.record-invalidated", error)
        VaultOperation.Failed(UnlockFailure.KeyInvalidated)
    } catch (error: VaultKeyUnavailableException) {
        // The stored record is untouched: an unavailable authenticator is not evidence of tampering.
        VaultDiagnostics.failure("vault.load.unavailable", error)
        VaultOperation.Failed(UnlockFailure.Unavailable)
    } catch (error: VaultTamperException) {
        VaultDiagnostics.failure("vault.load.tampered", error)
        VaultOperation.Failed(UnlockFailure.Tampered)
    } catch (error: CancellationException) {
        throw error
    } catch (error: Exception) {
        unnameableVaultFailure(operation = "load", error = error)
    }

    /**
     * Reads [record] without raising a confirmation prompt.
     *
     * Only meaningful with a device-unlock window key (D3): while the window is still open the Keystore
     * key can be used again without a new authorization. It is never a gate — an expired window, a locked
     * device or a dead key all come back as a failure so the caller can fall through to the authorized
     * [load]. Reading the record through the vault keeps the only security boundary in one place.
     */
    fun loadWithoutPrompt(record: VaultRecord): VaultOperation<CharArray> = try {
        VaultDiagnostics.trace("vault.load.unattended", "kind=${record.kind.name} reading inside an open window")
        VaultOperation.Success(vault.open(record, vault.beginOpen(record)))
    } catch (error: VaultKeyInvalidatedException) {
        VaultDiagnostics.failure("vault.load.unattended.invalidated", error)
        VaultOperation.Failed(UnlockFailure.KeyInvalidated)
    } catch (error: VaultRecordInvalidatedException) {
        VaultDiagnostics.failure("vault.load.unattended.record-invalidated", error)
        VaultOperation.Failed(UnlockFailure.KeyInvalidated)
    } catch (error: VaultKeyUnavailableException) {
        VaultDiagnostics.failure("vault.load.unattended.unavailable", error)
        VaultOperation.Failed(UnlockFailure.Unavailable)
    } catch (error: VaultTamperException) {
        VaultDiagnostics.failure("vault.load.unattended.tampered", error)
        VaultOperation.Failed(UnlockFailure.Tampered)
    } catch (error: Exception) {
        // Same rule as the attended read: an unattended read is an optimization, so any surprise is
        // simply "not this time" and the caller falls through to the authorized path.
        unnameableVaultFailure(operation = "load.unattended", error = error)
    }
}
