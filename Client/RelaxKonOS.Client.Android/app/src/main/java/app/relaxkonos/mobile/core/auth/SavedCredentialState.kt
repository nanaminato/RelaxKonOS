package app.relaxkonos.mobile.core.auth

import app.relaxkonos.mobile.security.VaultRecord
import app.relaxkonos.mobile.security.VaultRecordState
import app.relaxkonos.mobile.security.VaultUnlockMode

/**
 * Whether a saved password exists for one identity, and whether it can be used right now.
 *
 * The four states are mutually exclusive, and none of them is derived from the password field: a
 * screen asking "is there a saved password?" must ask this, never whether a text field happens to be
 * showing dots (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §2, §3).
 *
 * [Unavailable] and [Invalidated] are separate on purpose. An authenticator that cannot be used right
 * now — the master switch is off, the device has no usable lock screen, the key is temporarily
 * unusable — leaves the record **valid**: re-enrolling a fingerprint or re-enabling the switch makes
 * it work again. Only a permanently invalidated key makes the stored ciphertext unreadable. Telling a
 * user "this password has expired" when it has not makes them re-save it for nothing, which is exactly
 * what V1 §5.4 forbids.
 */
sealed interface SavedCredentialState {
    /** No credential is stored on this device for this identity. */
    data object Absent : SavedCredentialState

    /** A record exists but cannot be unsealed here and now. The record stays valid. */
    data object Unavailable : SavedCredentialState

    /** A record exists and can be unsealed after the matching authorization. */
    data object Available : SavedCredentialState

    /**
     * A record exists but its key is permanently gone. The record and its ciphertext are kept and
     * never read again; only an explicit delete removes it (D5).
     */
    data object Invalidated : SavedCredentialState
}

/**
 * Derives the state of one identity's saved password from the vault record and the device's current
 * ability to unlock it. Pure, so every combination is unit-testable without Android.
 *
 * [VaultRecordState.Invalidated] outranks a missing unlock mode: a permanently dead key stays reported
 * as dead even on a device that could not unlock anything anyway, because the two facts call for
 * different sentences and different user actions.
 */
fun credentialState(record: VaultRecord?, unlockMode: VaultUnlockMode?): SavedCredentialState = when {
    record == null -> SavedCredentialState.Absent
    record.state == VaultRecordState.Invalidated -> SavedCredentialState.Invalidated
    unlockMode == null -> SavedCredentialState.Unavailable
    else -> SavedCredentialState.Available
}

/**
 * Reconciles the vault's answer with the debug-only plaintext store.
 *
 * The store is a fallback, never an override: a usable vault record always wins because it is the
 * protected one. This only fills the two states that mean "nothing usable can be unsealed here" —
 * nothing stored, or stored but unusable on this device — which is exactly the state a device with no
 * lock screen is in. It deliberately does not resurrect an [SavedCredentialState.Invalidated] record:
 * the user has to be told that the old password is dead rather than quietly handed a different one.
 */
fun credentialState(vaultState: SavedCredentialState, debugFallbackAvailable: Boolean): SavedCredentialState =
    if (debugFallbackAvailable && vaultState != SavedCredentialState.Available && vaultState != SavedCredentialState.Invalidated) {
        SavedCredentialState.Available
    } else {
        vaultState
    }

/**
 * What a screen shows about the saved password: one line, outside the password field.
 *
 * Kept separate from [SavedCredentialState] so the same state can be described differently per screen
 * (a status line on the sign-in form, a row label in the connection list) without either screen
 * re-deriving the meaning from the record.
 */
enum class CredentialStatus {
    /** Nothing saved: no status line at all. */
    None,

    /** Saved and unlocked by a strong biometric, per use. */
    SavedByFingerprint,

    /** Saved and unlocked through the device credential window (D3, connection vault only). */
    SavedByScreenLock,

    /** Saved in the debug-only plaintext store. Says so, because nothing protects it. */
    SavedInDebugBuild,

    /** Saved, but not unlockable on this device right now. */
    Unavailable,

    /** Saved, but the key is permanently gone. */
    Invalidated,
}

/**
 * Maps a credential state onto the line a screen renders. Pure so the mapping cannot drift.
 *
 * [fromDebugFallback] is set only when the debug-only store supplied the credential. It outranks the
 * state on purpose: the line's job is to say what protects the password, and on that path the answer
 * is "nothing" — reading "unlock it with the screen lock" there would be a lie.
 */
fun credentialStatus(
    state: SavedCredentialState,
    unlockMode: VaultUnlockMode?,
    fromDebugFallback: Boolean = false,
): CredentialStatus = when {
    fromDebugFallback -> CredentialStatus.SavedInDebugBuild
    state == SavedCredentialState.Absent -> CredentialStatus.None
    state == SavedCredentialState.Unavailable -> CredentialStatus.Unavailable
    state == SavedCredentialState.Invalidated -> CredentialStatus.Invalidated
    unlockMode == VaultUnlockMode.DeviceUnlockWindow -> CredentialStatus.SavedByScreenLock
    else -> CredentialStatus.SavedByFingerprint
}
