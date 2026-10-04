package app.relaxkonos.mobile.ui.connect

import app.relaxkonos.mobile.servercenter.SshLoginTunnelProfile

/**
 * The identity a connection-list row or a pairing code puts into the sign-in form.
 *
 * Picking an identity also picks **how it is reached**, and the two are not separable: a paired device
 * key, a saved password and a managed login each run either over a direct address or over a tunnel, and
 * a form that kept the previous choice on screen asked a device-key sign-in for the login identity it
 * never uses (`LoginCredentials.Design.md` §5.1/§6.3). Every selection path builds one of these and
 * hands it to a single applier, so no path can half-apply a transport by forgetting a field.
 */
sealed interface LoginSelection {
    /** What the address field must show. Empty when the identity has no persistable address. */
    val serverUrl: String

    /** What the login-identity field must show. Empty when the identity is a key, not an account. */
    val identifier: String

    /** Whether this is the one selection that reaches its server through the SSH tunnel. */
    val requiresTunnel: Boolean get() = this is Tunnel

    /** A direct server: the address field stays editable and carries the identity's service id. */
    data class Direct(override val serverUrl: String, override val identifier: String) : LoginSelection

    /**
     * A managed login. It has no address to fill in on purpose: its transport address is the tunnel's
     * loopback port, which only exists once that tunnel is open, so the field stays empty and the host
     * is named instead (`LoginCredentials.Design.md` §6.3).
     */
    data class Managed(val hostDisplayName: String?, override val identifier: String) : LoginSelection {
        override val serverUrl: String get() = ""
    }

    /**
     * An SSH profile. The address is the remote server **inside** the tunnel — `127.0.0.1` from that
     * machine's point of view — never the random local port this device is about to listen on.
     */
    data class Tunnel(val profile: SshLoginTunnelProfile, override val identifier: String) : LoginSelection {
        override val serverUrl: String get() = profile.remoteUrl
    }
}
