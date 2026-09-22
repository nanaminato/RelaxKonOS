package app.relaxkonos.mobile.security.model

/**
 * A server the user has connected to before.
 *
 * Deliberately carries no secret: whether a password is available is answered by the connection vault
 * in `security/CredentialVault.kt`, so there is exactly one source of truth for that question and the
 * profile list cannot drift out of step with the vault (`RelaxKonOS.Mobile.V1.Design.md` §5.2).
 */
data class SavedConnection(
    val serverUrl: String,
    val identifier: String,
    val lastUsedEpochMillis: Long,
)
