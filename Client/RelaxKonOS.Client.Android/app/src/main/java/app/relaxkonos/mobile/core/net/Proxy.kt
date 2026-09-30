package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant

enum class ProxyRuntimeState(val wire: String) {
    NotInstalled("notInstalled"), Installing("installing"), Stopped("stopped"), Starting("starting"), Running("running"),
    Reloading("reloading"), Stopping("stopping"), Updating("updating"), Recovering("recovering"), Degraded("degraded"), Failed("failed")
}
enum class ProxyOperationState(val wire: String) {
    Queued("queued"), Running("running"), Succeeded("succeeded"), Failed("failed"), Cancelled("cancelled"), Interrupted("interrupted");
    val active get() = this == Queued || this == Running
}
enum class ProxyRoutingMode(val wire: String) { Rule("rule"), Global("global"), Direct("direct") }
enum class ProxyDownloadRoute(val wire: String) { Direct("direct"), SystemProxy("systemProxy") }
enum class ProxyAction(val kind: String) {
    Start("lifecycle.start"), Stop("lifecycle.stop"), Restart("lifecycle.restart"),
    RefreshSubscription("subscription.refresh"), ActivateSubscription("subscription.activate"), RefreshAll("subscription.refresh_all"),
    EnableTun("tun.enable"), DisableTun("tun.disable"), EmergencyDisableTun("tun.emergency-disable")
}
data class ProxyRuntime(val mode: String, val state: ProxyRuntimeState, val version: String?, val previousVersion: String?, val integrityVerified: Boolean, val problemCode: String)
data class ProxyProfile(val id: String, val name: String, val engineId: String, val active: Boolean, val revision: Long)
data class ProxySubscription(val id: String, val name: String, val profileId: String, val active: Boolean, val lastUpdatedAtMillis: Long?)
data class ProxyGroup(val name: String, val type: String, val selected: String?, val proxies: List<String>) {
    val selectable get() = type.equals("Selector", true)
}
data class ProxyOverview(val runtime: ProxyRuntime, val activeProfile: ProxyProfile?, val controllerReachable: Boolean,
    val managementRouteSafe: Boolean, val health: String, val problemCode: String, val supportsGroups: Boolean,
    val supportsValidation: Boolean, val supportsTun: Boolean, val operatingSystem: String?, val recoveryRequired: Boolean,
    val supportsConnections: Boolean = false, val supportsLogs: Boolean = false, val supportsDns: Boolean = false,
    val supportsAutoRoute: Boolean = false, val supportsDnsHijack: Boolean = false, val tunState: String = "disabled")
data class ProxyOperation(val operationId: String, val kind: String, val state: ProxyOperationState, val stage: String, val problemCode: String)
data class ProxyDelay(val proxyName: String, val delayMilliseconds: Int?, val timedOut: Boolean, val problemCode: String)
data class ProxyDownload(val version: String, val url: String)
data class ProxyProfileRequest(val name: String, val expectedRevision: Long? = null) {
    fun body(): JsonBody { require(name.isNotBlank() && name.length <= 128); return JsonBody().string("name", name.trim()).string("engineId", "mihomo").raw("expectedRevision", expectedRevision?.toString() ?: "null") }
}
class ProxyImportRequest(val url: String, val name: String?, val route: ProxyDownloadRoute) {
    fun body() = JsonBody().string("url", url).string("name", name?.trim()?.takeIf(String::isNotEmpty)).string("downloadRoute", route.wire)
    override fun toString() = "ProxyImportRequest(source=<redacted>)"
}
object ProxyRoutes {
    const val ROOT = "/api/v1.0/proxy"
    const val PROFILES = "$ROOT/profiles"
    const val SUBSCRIPTIONS = "$ROOT/subscriptions"
    const val GROUPS = "$ROOT/groups"
    const val ROUTING = "$ROOT/routing"
    fun segment(value: String): String { require(value.isNotEmpty() && value.length <= 512 && value.none { it.code < 32 }); return java.net.URLEncoder.encode(value, "UTF-8").replace("+", "%20") }
    fun profile(id: String) = "$PROFILES/${InstallationRoutes.canonicalId(id)}"
    fun subscription(id: String) = "$SUBSCRIPTIONS/${InstallationRoutes.canonicalId(id)}"
    fun operation(id: String) = "$ROOT/operations/${InstallationRoutes.canonicalId(id)}"
    fun selection(group: String) = "$GROUPS/${segment(group)}/selection"
    fun delay(group: String, proxy: String) = "$GROUPS/${segment(group)}/proxies/${segment(proxy)}/delay"
    fun action(action: ProxyAction, target: String?): String = when (action) {
        ProxyAction.Start, ProxyAction.Stop, ProxyAction.Restart -> "$ROOT/lifecycle/${action.name.lowercase()}"
        ProxyAction.RefreshSubscription -> "${subscription(requireNotNull(target))}/refresh"
        ProxyAction.ActivateSubscription -> "${subscription(requireNotNull(target))}/activate"
        ProxyAction.RefreshAll -> "$SUBSCRIPTIONS/refresh"
        ProxyAction.EnableTun -> "$ROOT/tun/enable"
        ProxyAction.DisableTun -> "$ROOT/tun/disable"
        ProxyAction.EmergencyDisableTun -> "$ROOT/tun/emergency-disable"
    }
}
object ProxyWire {
    private fun JSONObject.text(key: String): String? = if (isNull(key)) null else getString(key)
    private fun id(j: JSONObject, key: String) = InstallationRoutes.canonicalId(j.getString(key))
    fun runtime(payload: String) = runtime(JSONObject(payload))
    private fun runtime(j: JSONObject) = ProxyRuntime(j.getString("mode").also { require(it in setOf("none", "managed", "external")) },
        ProxyRuntimeState.entries.single { it.wire == j.getString("state") }, j.text("version"), j.text("previousVersion"), j.getBoolean("integrityVerified"), j.getString("problemCode"))
    fun profile(payload: String) = profile(JSONObject(payload))
    private fun profile(j: JSONObject) = ProxyProfile(id(j, "id"), j.getString("name"), j.getString("engineId").also { require(it == "mihomo") }, j.getBoolean("isActive"), j.getLong("revision").also { require(it > 0) })
    fun profiles(payload: String) = array(payload) { profile(it) }
    fun subscription(payload: String) = subscription(JSONObject(payload))
    private fun subscription(j: JSONObject) = ProxySubscription(id(j, "id"), j.getString("name"), id(j, "profileId"), j.getBoolean("isActive"), j.text("lastUpdatedAt")?.let { Instant.parse(it).toEpochMilli() })
    fun subscriptions(payload: String) = array(payload) { subscription(it) }
    fun overview(payload: String) = JSONObject(payload).let { j ->
        val health = j.getJSONObject("health"); val engine = j.getJSONObject("engineCapabilities")
        ProxyOverview(runtime(j.getJSONObject("runtime")), if (j.isNull("activeProfile")) null else profile(j.getJSONObject("activeProfile")),
            health.getBoolean("controllerReachable"), health.getBoolean("managementRouteSafe"), health.getString("state"), health.getString("problemCode"),
            engine.getBoolean("supportsGroups"), engine.getBoolean("supportsConfigurationValidation"), j.getJSONObject("platformCapabilities").getBoolean("supportsTun"),
            j.text("operatingSystem"), j.getJSONObject("recovery").getBoolean("recoveryRequired"),
            engine.getBoolean("supportsConnections"), engine.getBoolean("supportsBoundedLogs"), engine.getBoolean("supportsDnsStatus"),
            j.getJSONObject("platformCapabilities").getBoolean("supportsAutoRoute"), j.getJSONObject("platformCapabilities").getBoolean("supportsDnsHijack"),
            health.getString("tunState").also { require(it in setOf("disabled", "enabling", "enabled", "disabling", "recovering", "failed")) })
    }
    fun groups(payload: String) = array(payload) { j -> ProxyGroup(j.getString("name"), j.getString("type"), j.text("selected"), j.getJSONArray("proxies").let { a -> List(a.length()) { a.getString(it) } }) }
    fun routing(payload: String) = JSONObject(payload).let { j -> require(j.getString("problemCode").isBlank()); ProxyRoutingMode.entries.single { it.wire == j.getString("mode") } }
    fun accepted(payload: String) = id(JSONObject(payload), "operationId")
    fun operation(payload: String) = JSONObject(payload).let { j -> ProxyOperation(id(j, "operationId"), j.getString("kind"), ProxyOperationState.entries.single { it.wire == j.getString("state") }, j.getString("stage"), j.getString("problemCode")) }
    fun delay(payload: String) = JSONObject(payload).let { j -> ProxyDelay(j.getString("proxyName"), if (j.isNull("delayMilliseconds")) null else j.getInt("delayMilliseconds").also { require(it >= 0) }, j.getBoolean("timedOut"), j.getString("problemCode")) }
    fun download(payload: String) = JSONObject(payload).let { ProxyDownload(it.getString("version"), it.getString("url")) }
    fun downloadOptions(payload: String) = JSONObject(payload).getBoolean("systemProxyAvailable")
    private fun <T> array(payload: String, parse: (JSONObject) -> T) = JSONArray(payload).let { a -> require(a.length() <= 10000); List(a.length()) { parse(a.getJSONObject(it)) } }
}
