package app.relaxkonos.mobile.security.model

import app.relaxkonos.mobile.core.auth.loginId
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.recordId
import app.relaxkonos.mobile.servercenter.ServerInstallationId
import app.relaxkonos.mobile.servercenter.ServerServiceIdKind

/**
 * A remembered login. [serviceId] is a canonical URL for direct connections and a verified
 * installation id for managed tunnels. A temporary loopback address is never persisted here.
 */
data class SavedLogin(
    val serviceId: String,
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
    val id: String get() = loginId(serviceId, identifier)

    /**
     * The vault record that holds this login's password.
     *
     * An internal association only: it identifies a record, never contains a password, and is not shown
     * on screen or written to logs and diagnostic exports (§12).
     */
    val credentialKey: String get() = recordId(VaultKind.Connection, serviceId, identifier)

    val serviceIdKind: ServerServiceIdKind
        get() = if (ServerInstallationId.isValid(serviceId)) {
            ServerServiceIdKind.ManagedInstallation
        } else ServerServiceIdKind.DirectUrl

    /** Direct profiles may refill the URL field; managed profiles must first resolve an SSH tunnel. */
    val directServerUrl: String? get() = serviceId.takeIf { serviceIdKind == ServerServiceIdKind.DirectUrl }
}
