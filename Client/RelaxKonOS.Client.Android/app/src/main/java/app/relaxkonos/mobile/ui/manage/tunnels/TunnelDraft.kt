package app.relaxkonos.mobile.ui.manage.tunnels

import app.relaxkonos.mobile.core.net.*

internal object TunnelInputs {
    fun name(value: String) = value.trim().matches(Regex("[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}"))
    fun host(value: String): String? = CertificateNames.normalize(value)?.takeUnless { it.startsWith("*.") }
    fun port(value: String) = value.toIntOrNull()?.takeIf { it in 1..65535 }
    fun absolutePath(value: String) = value.startsWith('/') || value.matches(Regex("[A-Za-z]:[\\\\/].+")) || value.startsWith("\\\\")
}
internal data class TunnelProfileDraft(val id: String? = null, val revision: Long? = null, val name: String = "", val host: String = "", val port: String = "7000",
    val auth: TunnelAuth = TunnelAuth.Token, val tls: TunnelTls = TunnelTls.Default, val mode: TunnelRuntimeMode = TunnelRuntimeMode.Managed, val path: String = "") {
    fun request(): TunnelProfileRequest? {
        if (!TunnelInputs.name(name) || mode == TunnelRuntimeMode.External && !TunnelInputs.absolutePath(path.trim())) return null
        return TunnelProfileRequest(name.trim(), TunnelInputs.host(host) ?: return null, TunnelInputs.port(port) ?: return null, auth, tls, mode,
            path.trim().takeIf { mode == TunnelRuntimeMode.External }, revision)
    }
    companion object { fun from(p: TunnelProfile) = TunnelProfileDraft(p.id, p.revision, p.name, p.host, p.port.toString(), p.auth, p.tls, p.runtimeMode, p.externalPath.orEmpty()) }
}
internal data class TunnelDefinitionDraft(val id: String? = null, val revision: Long? = null, val profileId: String = "", val name: String = "",
    val protocol: TunnelProtocol = TunnelProtocol.Tcp, val localHost: String = "127.0.0.1", val localPort: String = "", val remotePort: String = "", val domain: String = "",
    val enabled: Boolean = true, val encryption: Boolean = false, val compression: Boolean = false) {
    fun request(): TunnelDefinitionRequest? {
        if (!TunnelInputs.name(name) || runCatching { InstallationRoutes.canonicalId(profileId) }.isFailure) return null
        val host = TunnelInputs.host(localHost) ?: return null
        val local = TunnelInputs.port(localPort) ?: return null
        val remote = if (protocol.usesPort) TunnelInputs.port(remotePort) ?: return null else null
        val dns = if (protocol.usesPort) null else (TunnelInputs.host(domain)?.takeUnless { it.contains(':') || it.matches(Regex("\\d+\\.\\d+\\.\\d+\\.\\d+")) } ?: return null)
        return TunnelDefinitionRequest(profileId, name.trim(), protocol, host, local, remote, dns, enabled, encryption, compression, revision)
    }
    companion object { fun from(d: TunnelDefinition) = TunnelDefinitionDraft(d.id, d.revision, d.profileId, d.name, d.protocol, d.localHost, d.localPort.toString(), d.remotePort?.toString().orEmpty(), d.domain.orEmpty(), d.enabled, d.encryption, d.compression) }
}
