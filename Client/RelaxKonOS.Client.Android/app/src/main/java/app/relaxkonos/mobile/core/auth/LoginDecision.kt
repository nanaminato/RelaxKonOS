package app.relaxkonos.mobile.core.auth

/**
 * What the single sign-in button must do on this click.
 *
 * The design keeps one button whose *label* follows the decision, instead of two buttons that can each
 * fail to fall back to the other (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §5.1, G1).
 */
sealed interface LoginDecision {
    /**
     * Use the password typed into the form this time.
     *
     * The caller owns [password] and must zero it once the request has finished. A saved credential for
     * the same identity is ignored by this click but is **not** deleted or overwritten (§5.1 row 3).
     */
    class ManualPassword(val password: CharArray) : LoginDecision

    /** Unseal the saved password first (authorization is a biometric or the device credential). */
    data object UnlockSavedCredential : LoginDecision

    /** Nothing usable is stored: the password must be typed. [gap] decides the reason shown. */
    data class RequirePassword(val gap: CredentialGap) : LoginDecision

    /** The address or the account is still empty, so no request may be sent. */
    data class MissingFields(val server: Boolean, val identifier: Boolean) : LoginDecision
}

/** Why a password has to be typed instead of unsealed. */
enum class CredentialGap {
    /** No credential is stored for this identity. */
    Absent,

    /** A credential is stored but cannot be unsealed on this device right now. */
    Unavailable,

    /** A credential is stored but its key is permanently gone. */
    Invalidated,
}

/**
 * The whole sign-in decision, as one pure function of four observable inputs.
 *
 * Order matters and is fixed by the design:
 *
 * 1. incomplete fields win over everything, including "already signing in" — the user is told what is
 *    missing rather than having the click silently swallowed;
 * 2. a click while a sign-in is in flight is ignored (returns `null`);
 * 3. a non-empty password field beats a saved credential, so typing always means "use what I typed";
 * 4. otherwise a usable saved credential is unsealed;
 * 5. otherwise the password has to be typed, and [CredentialGap] explains why.
 *
 * Empty passwords are never submitted: `LoginRequest.password` is required and the server rejects it
 * (§5.1, D8). The password field is compared for emptiness rather than blankness because a password may
 * legitimately contain spaces, and trimming a password would send something the user did not type.
 */
fun decideLogin(
    selected: SelectedLogin,
    passwordText: String,
    credential: SavedCredentialState,
    isLoggingIn: Boolean,
): LoginDecision? = when {
    !selected.isComplete -> LoginDecision.MissingFields(
        server = selected.serverUrl.isBlank(),
        identifier = selected.identifier.isBlank(),
    )

    isLoggingIn -> null

    passwordText.isNotEmpty() -> LoginDecision.ManualPassword(passwordText.toCharArray())

    credential == SavedCredentialState.Available -> LoginDecision.UnlockSavedCredential

    credential == SavedCredentialState.Unavailable ->
        LoginDecision.RequirePassword(CredentialGap.Unavailable)

    credential == SavedCredentialState.Invalidated ->
        LoginDecision.RequirePassword(CredentialGap.Invalidated)

    else -> LoginDecision.RequirePassword(CredentialGap.Absent)
}
