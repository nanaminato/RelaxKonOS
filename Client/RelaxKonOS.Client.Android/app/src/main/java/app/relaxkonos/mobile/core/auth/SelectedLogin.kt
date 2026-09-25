package app.relaxkonos.mobile.core.auth

import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.recordId
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentity
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import app.relaxkonos.mobile.servercenter.ServerServiceIdKind

/** Stable identity of one login: `(serviceId, identifier)`. */
fun loginId(serviceId: String, identifier: String): String = "${serviceId.trim()}|${identifier.trim()}"

/**
 * The identity and current transport address used by one sign-in attempt. [serviceId] is the only
 * value used by profiles and credential stores. [effectiveBaseUrl] is only where this attempt sends
 * HTTP and may change when an SSH tunnel is rebound.
 */
data class SelectedLogin(
    val kind: ServerServiceIdKind,
    val serviceId: String,
    val effectiveBaseUrl: String,
    val identifier: String,
) {
    val normalizedIdentifier: String get() = identifier.trim()

    val isComplete: Boolean
        get() = serviceId.isNotBlank() && effectiveBaseUrl.isNotBlank() && identifier.isNotBlank()

    val id: String get() = loginId(serviceId, normalizedIdentifier)

    val credentialKey: String
        get() = recordId(VaultKind.Connection, serviceId, normalizedIdentifier)

    val connectionIdentity: ServerConnectionIdentity
        get() = ServerConnectionIdentity(kind, serviceId, effectiveBaseUrl)

    companion object {
        /** Invalid partial text remains representable while the user is typing. */
        fun direct(serverUrl: String, identifier: String): SelectedLogin {
            val raw = serverUrl.trim().trimEnd('/')
            val identity = runCatching { ServerConnectionIdentityRules.direct(raw) }.getOrElse {
                ServerConnectionIdentity(ServerServiceIdKind.DirectUrl, raw, raw)
            }
            return SelectedLogin(identity.kind, identity.serviceId, identity.effectiveBaseUrl, identifier)
        }

        fun resolved(identity: ServerConnectionIdentity, identifier: String): SelectedLogin =
            SelectedLogin(identity.kind, identity.serviceId, identity.effectiveBaseUrl, identifier)
    }
}
