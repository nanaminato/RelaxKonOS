package app.relaxkonos.mobile.core.net

import java.net.URI
import org.json.JSONArray
import org.json.JSONObject

/** Full site definition. Certificate/key paths refer to host files; key contents never enter the app. */
data class WebServerSiteRequest(val id: String, val name: String, val bindings: List<WebServerBinding>,
    val rootPath: String?, val grantNginxReadAccess: Boolean, val spaFallback: Boolean,
    val routes: List<WebServerRoute>, val certificateId: String?, val httpsEnabled: Boolean,
    val redirectHttpToHttps: Boolean, val ipv6Enabled: Boolean,
    val certificatePath: String?, val privateKeyPath: String?, val expectedUpdatedAt: String?) {
    fun body(): JsonBody = JsonBody().string("id", id).string("name", name).raw("bindings", JSONArray().apply {
        bindings.forEach { put(JSONObject().put("domain", it.domain).put("port", it.port)) }
    }.toString()).string("rootPath", rootPath).bool("grantNginxReadAccess", grantNginxReadAccess)
        .bool("spaFallback", spaFallback).raw("routes", JSONArray().apply {
            routes.forEach { put(JSONObject().put("path", it.path).put("upstream", it.upstream).put("disableBuffering", it.disableBuffering)) }
        }.toString()).string("certificateId", certificateId).bool("httpsEnabled", httpsEnabled)
        .bool("redirectHttpToHttps", redirectHttpToHttps).bool("ipv6Enabled", ipv6Enabled)
        .string("certificatePath", certificatePath).string("privateKeyPath", privateKeyPath).string("expectedUpdatedAt", expectedUpdatedAt)
}

/** Only factual content agreement, never proof that a lost request or a filesystem permission grant succeeded. */
fun WebServerSiteRequest.matches(site: WebServerSite): Boolean {
    fun domain(value: String) = value.trim().trimEnd('.').lowercase()
    fun upstream(value: String) = runCatching { URI(value.trim()).normalize().toASCIIString().trimEnd('/') }.getOrDefault(value.trim())
    return site.id == id && site.name == name.trim() && site.rootPath == rootPath?.trim() && site.spaFallback == spaFallback &&
        site.bindings.map { domain(it.domain) to it.port }.toSet() == bindings.map { domain(it.domain) to it.port }.toSet() &&
        site.routes.map { Triple(it.path, upstream(it.upstream), it.disableBuffering) } == routes.map { Triple(it.path.trim(), upstream(it.upstream), it.disableBuffering) } &&
        site.certificateId == certificateId && site.httpsEnabled == httpsEnabled && site.redirectHttpToHttps == redirectHttpToHttps &&
        site.ipv6Enabled == ipv6Enabled && site.certificatePath == certificatePath?.trim() && site.privateKeyPath == privateKeyPath?.trim()
}
