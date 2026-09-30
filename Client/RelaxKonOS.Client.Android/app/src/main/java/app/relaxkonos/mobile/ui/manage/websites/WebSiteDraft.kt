package app.relaxkonos.mobile.ui.manage.websites

import app.relaxkonos.mobile.core.net.*
import java.net.URI
import java.util.UUID

internal data class SiteBindingDraft(val domain: String = "", val port: String = "80")
internal data class SiteRouteDraft(val path: String = "/", val upstream: String = "", val disableBuffering: Boolean = false)
internal data class WebSiteDraft(val serverId: String, val id: String = "site-${UUID.randomUUID()}", val expectedUpdatedAt: String? = null,
    val name: String = "", val bindings: List<SiteBindingDraft> = listOf(SiteBindingDraft()),
    val rootPath: String = "", val spaFallback: Boolean = false, val grantReadAccess: Boolean = false,
    val routes: List<SiteRouteDraft> = emptyList(), val certificateId: String? = null,
    val httpsEnabled: Boolean = false, val redirectHttpToHttps: Boolean = false, val ipv6Enabled: Boolean = false,
    val useServerCertificate: Boolean = false, val certificatePath: String = "", val privateKeyPath: String = "") {
    fun request(): WebServerSiteRequest? {
        val ports = bindings.map { it.port.toIntOrNull() }
        if (name.trim().length !in 1..80 || bindings.size !in 1..20 || ports.any { it == null || it !in 1..65535 } ||
            bindings.any { it.domain.isBlank() || it.domain.any(Char::isWhitespace) } || (rootPath.isBlank() && routes.isEmpty()) ||
            (grantReadAccess && rootPath.isBlank()) || (redirectHttpToHttps && !httpsEnabled) ||
            (httpsEnabled && if (useServerCertificate) certificatePath.isBlank() else certificateId == null)) return null
        if (!useServerCertificate && certificateId?.let { runCatching { InstallationRoutes.operation(it) }.isFailure } == true) return null
        if (routes.map { it.path.trim() }.distinct().size != routes.size || routes.any { route ->
            !route.path.trim().matches(Regex("^/(?:[A-Za-z0-9._~-]+/)*$")) || runCatching {
                val uri = URI(route.upstream.trim()); uri.scheme !in setOf("http", "https") || uri.host.isNullOrBlank() || uri.userInfo != null || uri.fragment != null
            }.getOrDefault(true)
        }) return null
        return WebServerSiteRequest(id, name.trim(), bindings.mapIndexed { i, binding -> WebServerBinding(binding.domain.trim(), ports[i]!!) },
            rootPath.trim().takeIf(String::isNotBlank), grantReadAccess, spaFallback,
            routes.map { WebServerRoute(it.path.trim(), it.upstream.trim(), it.disableBuffering) },
            if (useServerCertificate) null else certificateId, httpsEnabled, redirectHttpToHttps, ipv6Enabled,
            if (useServerCertificate) certificatePath.trim().takeIf(String::isNotBlank) else null,
            if (useServerCertificate) privateKeyPath.trim().takeIf(String::isNotBlank) else null, expectedUpdatedAt)
    }
    companion object {
        fun from(site: WebServerSite) = WebSiteDraft(site.serverId, site.id, site.updatedAt, site.name,
            site.bindings.map { SiteBindingDraft(it.domain, it.port.toString()) }, site.rootPath.orEmpty(), site.spaFallback,
            routes = site.routes.map { SiteRouteDraft(it.path, it.upstream, it.disableBuffering) }, certificateId = site.certificateId,
            httpsEnabled = site.httpsEnabled, redirectHttpToHttps = site.redirectHttpToHttps, ipv6Enabled = site.ipv6Enabled,
            useServerCertificate = site.certificatePath != null, certificatePath = site.certificatePath.orEmpty(), privateKeyPath = site.privateKeyPath.orEmpty())
    }
}
