package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.json.JSONArray
import java.time.Instant

data class ProxyTunSettings(val stack: String, val deviceName: String, val autoRoute: Boolean, val strictRoute: Boolean,
    val autoDetectInterface: Boolean, val dnsHijack: String, val mtu: Int) {
    fun body() = JsonBody().string("stack", stack).string("deviceName", deviceName).bool("autoRoute", autoRoute)
        .bool("strictRoute", strictRoute).bool("autoDetectInterface", autoDetectInterface).string("dnsHijack", dnsHijack).int("mtu", mtu)
}
data class ProxySystemOptions(val usePac: Boolean, val guardEnabled: Boolean, val guardIntervalSeconds: Int, val useDefaultBypass: Boolean, val bypassList: String) {
    fun body() = JsonBody().bool("usePac", usePac).bool("guardEnabled", guardEnabled).int("guardIntervalSeconds", guardIntervalSeconds)
        .bool("useDefaultBypass", useDefaultBypass).string("bypassList", bypassList)
}
data class ProxySettings(val systemProxyEnabled: Boolean, val allowLan: Boolean, val dnsEnabled: Boolean, val ipv6Enabled: Boolean,
    val unifiedDelay: Boolean, val logLevel: String, val mixedPort: Int, val allowInsecureSubscriptionSources: Boolean, val systemProxyHost: String,
    val tun: ProxyTunSettings?, val systemProxy: ProxySystemOptions?) {
    fun body(): JsonBody {
        require(mixedPort in 1..65535 && logLevel in setOf("silent", "error", "warning", "info", "debug"))
        return JsonBody().bool("systemProxyEnabled", systemProxyEnabled).bool("allowLan", allowLan).bool("dnsEnabled", dnsEnabled)
            .bool("ipv6Enabled", ipv6Enabled).bool("unifiedDelay", unifiedDelay).string("logLevel", logLevel).int("mixedPort", mixedPort)
            .bool("allowInsecureSubscriptionSources", allowInsecureSubscriptionSources).string("systemProxyHost", systemProxyHost)
            .let { if (tun == null) it.raw("tun", "null") else it.objectField("tun", tun.body()) }
            .let { if (systemProxy == null) it.raw("systemProxy", "null") else it.objectField("systemProxy", systemProxy.body()) }
    }
}
data class ProxyRecovery(val recoveryRequired: Boolean, val hasMarker: Boolean, val markerCreatedAtMillis: Long?, val problemCode: String)
data class ProxyTraffic(val uploadPerSecond: Long, val downloadPerSecond: Long, val uploadTotal: Long, val downloadTotal: Long, val memoryBytes: Long, val problemCode: String)
data class ProxyConnection(val id: String, val network: String, val source: String, val destination: String, val rule: String, val chains: String, val startedAtMillis: Long)
data class ProxyLog(val timestampMillis: Long, val level: String, val message: String)
data class ProxyDns(val enabled: Boolean, val hijackEnabled: Boolean, val mode: String?, val problemCode: String)
data class ProxyGeoData(val configured: Boolean, val sizeBytes: Long?)
object ProxyDiagnosticsWire {
    fun settings(payload: String) = JSONObject(payload).let { j ->
        val tun = if (j.isNull("tun")) null else j.getJSONObject("tun").let { ProxyTunSettings(it.getString("stack"), it.getString("deviceName"), it.getBoolean("autoRoute"), it.getBoolean("strictRoute"), it.getBoolean("autoDetectInterface"), it.getString("dnsHijack"), it.getInt("mtu")) }
        val system = if (j.isNull("systemProxy")) null else j.getJSONObject("systemProxy").let { ProxySystemOptions(it.getBoolean("usePac"), it.getBoolean("guardEnabled"), it.getInt("guardIntervalSeconds"), it.getBoolean("useDefaultBypass"), it.getString("bypassList")) }
        ProxySettings(j.getBoolean("systemProxyEnabled"), j.getBoolean("allowLan"), j.getBoolean("dnsEnabled"), j.getBoolean("ipv6Enabled"), j.getBoolean("unifiedDelay"),
            j.getString("logLevel"), j.getInt("mixedPort").also { require(it in 1..65535) }, j.getBoolean("allowInsecureSubscriptionSources"), j.getString("systemProxyHost"), tun, system)
    }
    fun recovery(payload: String) = JSONObject(payload).let { ProxyRecovery(it.getBoolean("recoveryRequired"), it.getBoolean("hasRecoveryMarker"), if (it.isNull("markerCreatedAt")) null else Instant.parse(it.getString("markerCreatedAt")).toEpochMilli(), it.getString("problemCode")) }
    fun traffic(payload: String) = JSONObject(payload).let { j ->
        fun count(key: String) = j.getLong(key).also { require(it >= 0) }
        ProxyTraffic(count("uploadBytesPerSecond"), count("downloadBytesPerSecond"), count("uploadTotalBytes"), count("downloadTotalBytes"), count("memoryBytes"), j.getString("problemCode"))
    }
    fun connections(payload: String) = array(payload, 10000) { j -> ProxyConnection(j.getString("id"), j.getString("network"), j.getString("source"), j.getString("destination"), j.getString("rule"), j.getString("chains"), Instant.parse(j.getString("startedAt")).toEpochMilli()) }
    fun logs(payload: String) = array(payload, 500) { j -> ProxyLog(Instant.parse(j.getString("timestamp")).toEpochMilli(), j.getString("level"), j.getString("message")) }
    fun dns(payload: String) = JSONObject(payload).let { ProxyDns(it.getBoolean("enabled"), it.getBoolean("hijackEnabled"), if (it.isNull("mode")) null else it.getString("mode"), it.getString("problemCode")) }
    fun geoData(payload: String) = JSONObject(payload).let { ProxyGeoData(it.getBoolean("isConfigured"), if (it.isNull("sizeBytes")) null else it.getLong("sizeBytes").also { size -> require(size >= 0) }) }
    private fun <T> array(payload: String, max: Int, parse: (JSONObject) -> T) = JSONArray(payload).let { a -> require(a.length() <= max); List(a.length()) { parse(a.getJSONObject(it)) } }
}
