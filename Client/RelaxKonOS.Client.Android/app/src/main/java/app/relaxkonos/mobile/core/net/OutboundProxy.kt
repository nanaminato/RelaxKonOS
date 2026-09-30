package app.relaxkonos.mobile.core.net

import org.json.JSONObject

/** Current DockerProxyContracts projection. URLs can contain credentials; never persist or log it. */
enum class OutboundProxySource(val wire: String) { Custom("custom"), ManagedProxy("managedProxy") }
enum class OutboundProxyTarget(val wire: String) { Engine("engine"), Build("build") }
enum class OutboundProxyLayerState(val wire: String) {
    Disabled("disabled"), Applied("applied"), RestartRequired("restartRequired"), Unsupported("unsupported"), Failed("failed")
}
data class OutboundProxySettings(
    val enabled: Boolean, val source: OutboundProxySource, val httpProxy: String, val httpsProxy: String,
    val noProxy: String, val applyToEngine: Boolean, val applyToBuild: Boolean,
    val applyToImageTags: Boolean, val applyToRuntimeDownloads: Boolean,
)
data class OutboundProxyLayer(val target: OutboundProxyTarget, val state: OutboundProxyLayerState, val problemCode: String, val detail: String)
data class DesktopProxy(val mode: String, val httpProxy: String, val httpsProxy: String, val noProxy: String, val settingsPath: String)
data class OutboundProxyStatus(
    val settings: OutboundProxySettings, val layers: List<OutboundProxyLayer>,
    val effectiveHttpProxy: String, val effectiveHttpsProxy: String, val effectiveNoProxy: String,
    val managedProxyEndpoint: String, val managedProxyAvailable: Boolean, val platform: String,
    val desktopProxy: DesktopProxy?,
)

internal object OutboundProxyWire {
    const val ROUTE = "/api/v1.0/docker/proxy"
    fun request(settings: OutboundProxySettings, confirmed: Boolean): JsonBody = JsonBody()
        .bool("enabled", settings.enabled).string("source", settings.source.wire)
        .string("httpProxy", settings.httpProxy).string("httpsProxy", settings.httpsProxy)
        .string("noProxy", settings.noProxy).bool("applyToEngine", settings.applyToEngine)
        .bool("applyToBuild", settings.applyToBuild).bool("applyToImageTags", settings.applyToImageTags)
        .bool("applyToRuntimeDownloads", settings.applyToRuntimeDownloads).bool("confirmed", confirmed)

    fun status(body: String): OutboundProxyStatus = JSONObject(body).let { json ->
        val s = json.getJSONObject("settings")
        val settings = OutboundProxySettings(s.getBoolean("enabled"),
            OutboundProxySource.entries.single { it.wire == s.getString("source") },
            s.getString("httpProxy"), s.getString("httpsProxy"), s.getString("noProxy"),
            s.getBoolean("applyToEngine"), s.getBoolean("applyToBuild"),
            s.getBoolean("applyToImageTags"), s.getBoolean("applyToRuntimeDownloads"))
        val array = json.getJSONArray("layers")
        val layers = List(array.length()) { i -> array.getJSONObject(i).let { layer ->
            OutboundProxyLayer(OutboundProxyTarget.entries.single { it.wire == layer.getString("target") },
                OutboundProxyLayerState.entries.single { it.wire == layer.getString("state") },
                layer.getString("problemCode"), layer.getString("detail"))
        } }
        val desktop = if (json.isNull("desktopProxy")) null else json.getJSONObject("desktopProxy").let {
            DesktopProxy(it.getString("mode"), it.getString("httpProxy"), it.getString("httpsProxy"),
                it.getString("noProxy"), it.getString("settingsPath"))
        }
        OutboundProxyStatus(settings, layers, json.getString("effectiveHttpProxy"),
            json.getString("effectiveHttpsProxy"), json.getString("effectiveNoProxy"),
            json.getString("managedProxyEndpoint"), json.getBoolean("managedProxyAvailable"),
            json.getString("platform"), desktop)
    }
}
