package app.relaxkonos.mobile.security.model

import app.relaxkonos.mobile.core.auth.loginId
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.recordId

/**
 * A login the user has used before: one server address plus one login identifier.
 *
 * Deliberately carries no secret. The password lives in the connection vault, and whether it is
 * available is answered by that vault.
 *
 * The logical key is `(ServiceId, Username)` — here `(serverUrl, identifier)` — so the same server with
 * two accounts is two independent records, each with its own credential state. Nothing may look a login
 * up, or delete one, by server address alone (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §1, §2.2).
 */
data class SavedLogin(
    val serverUrl: String,
    val identifier: String,
    val lastUsedEpochMillis: Long,
    /**
     * Optional human-readable label. There is no server-side display name yet, so this stays absent
     * and is not rendered as an empty placeholder; a name is never invented from the identifier.
     */
    val displayName: String? = null,
    /**
     * Non-sensitive display projection: "a credential record exists for this login".
     *
     * It exists so a list can answer that question without opening the vault. It is **not** a security
     * boundary and not the source of truth — the vault is. Screens that must be accurate about *usability*
     * ask [app.relaxkonos.mobile.core.auth.credentialState]; this flag is reconciled against the vault on
     * every start so the two cannot drift apart (§2.2, §4.2, D6).
     */
    val hasSavedCredential: Boolean = false,
) {
    /** Stable local identity of this login: `serverUrl|identifier`. */
    val id: String get() = loginId(serverUrl, identifier)

    /**
     * The vault record that holds this login's password.
     *
     * An internal association only: it identifies a record, never contains a password, and is not shown
     * on screen or written to logs and diagnostic exports (§12).
     */
    val credentialKey: String get() = recordId(VaultKind.Connection, serverUrl, identifier)
}
