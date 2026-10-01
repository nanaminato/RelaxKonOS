package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.DeploymentApplication
import app.relaxkonos.mobile.core.net.WebServerSite
import app.relaxkonos.mobile.servercenter.*
import java.net.IDN
import java.net.URI
import java.util.Locale

data class ExternalServiceAddress(val url: String, val loopback: Boolean = false)

/** Only HTTP(S) service URLs. API base paths, credentials and transport-only login ports are not exported. */
object ExternalServiceAddresses {
    fun valid(value: String): Boolean = value.length <= 4096 && value.none { it.isISOControl() || it == '\\' } && runCatching {
        val uri = URI(value)
        uri.scheme in setOf("http", "https") && uri.host != null && uri.rawUserInfo == null && uri.fragment == null &&
            uri.port in -1..65535 && uri.port != 0 && uri.host !in setOf("0.0.0.0", "[::]", "::")
    }.getOrDefault(false)

    fun site(site: WebServerSite): List<ExternalServiceAddress> = site.bindings.flatMap { binding ->
        val host = host(binding.domain) ?: return@flatMap emptyList()
        if (loopback(host)) return@flatMap emptyList()
        buildList {
            // Current Nginx contract adds a TLS listener at 443, independent of HTTP bindings.
            if (site.httpsEnabled) address("https", host, 443)?.let(::add)
            if (!site.redirectHttpToHttps && (!site.httpsEnabled || binding.port != 443)) address("http", host, binding.port)?.let(::add)
        }
    }.distinctBy { it.url }

    fun deployment(owner: SessionState.Active, app: DeploymentApplication): ExternalServiceAddress? {
        if (app.workloadKind != "web" || app.actualState != "running" || app.hostPort == null || loopback(app.bindAddress)) return null
        // A managed login's phone loopback API address is never a remote workload address.
        if (ServerInstallationId.isValid(owner.serviceId)) return null
        val origin = runCatching { URI(owner.serviceId) }.getOrNull() ?: return null
        if (origin.scheme !in setOf("http", "https") || origin.rawUserInfo != null) return null
        val host = host(origin.host ?: return null) ?: return null
        if (loopback(host)) return null
        return address("http", host, app.hostPort)
    }
    fun forward(row: SshLocalForward): ExternalServiceAddress? =
        if (row.status == SshForwardStatus.Running && row.request.valid()) row.request.localUrl(row.localPort)
            .takeIf(::valid)?.let { ExternalServiceAddress(it, true) } else null
    private fun loopback(host: String): Boolean = ServerTunnelRules.isLoopbackHost(host) || host.equals("localhost", true) || host.endsWith(".localhost", true)
    private fun address(scheme: String, host: String, port: Int): ExternalServiceAddress? {
        if (port !in 1..65535) return null
        val value = runCatching { URI(scheme, null, host.removeSurrounding("[", "]"), if (scheme == "http" && port == 80 || scheme == "https" && port == 443) -1 else port, "/", null, null).toASCIIString() }.getOrNull() ?: return null
        return value.takeIf(::valid)?.let { ExternalServiceAddress(it) }
    }
    private fun host(value: String): String? {
        val normalized = value.trim().trimEnd('.').lowercase(Locale.ROOT)
        if (normalized.isBlank() || normalized == "_" || normalized.contains('*') || normalized.contains('%')) return null
        if (normalized.contains(':')) return normalized.takeIf { runCatching { URI("http://[${it.removeSurrounding("[", "]")}]/").host != null }.getOrDefault(false) }
        return runCatching { IDN.toASCII(normalized, IDN.USE_STD3_ASCII_RULES).takeIf { it.length <= 253 } }.getOrNull()
    }
}
