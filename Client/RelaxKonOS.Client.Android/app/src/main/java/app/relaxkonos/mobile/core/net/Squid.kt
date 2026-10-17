package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

data class SquidUpstream(val host: String, val port: Int)
data class SquidConfiguration(val bindAddress: String = "127.0.0.1", val port: Int = 3128,
    val allowRemoteClients: Boolean = false, val allowedNetworks: List<String> = listOf("127.0.0.1/32", "::1/128"),
    val allowedDomains: List<String> = emptyList(), val deniedDomains: List<String> = emptyList(), val upstream: SquidUpstream? = null) {
    fun json(): JSONObject = JSONObject().put("bindAddress", bindAddress).put("port", port).put("allowRemoteClients", allowRemoteClients)
        .put("allowedNetworks", JSONArray(allowedNetworks)).put("allowedDomains", JSONArray(allowedDomains)).put("deniedDomains", JSONArray(deniedDomains))
        .put("upstream", upstream?.let { JSONObject().put("host", it.host).put("port", it.port) } ?: JSONObject.NULL)
    fun body(): JsonBody = SquidWire.body(json())
}
data class SquidStatus(val supported: Boolean, val installed: Boolean, val running: Boolean, val enabled: Boolean,
    val managed: Boolean, val configurationRevision: String, val packageManagementSupported: Boolean, val recoveryRequired: Boolean)
data class SquidSnapshot(val configuration: SquidConfiguration?, val desiredRevision: Long, val publishedRevision: Long?,
    val lastProblemCode: String?, val status: SquidStatus, val matchesPublishedConfiguration: Boolean)
data class SquidPreview(val valid: Boolean, val configuration: String?, val errors: List<String>)
data class SquidValidation(val valid: Boolean, val problemCode: String?, val diagnostics: String)
enum class SquidAction { Start, Stop, Restart, Reload, Enable, Disable }
data class SquidRequest(val configuration: SquidConfiguration? = null, val adopt: Boolean = false, val action: SquidAction? = null) {
    init { require((configuration == null) != (action == null)) }
    val route get() = SquidWire.ROOT + if (action == null) "/configuration/apply" else "/lifecycle"
    fun json(): JSONObject = action?.let { JSONObject().put("action", it.name.lowercase()) }
        ?: JSONObject().put("configuration", requireNotNull(configuration).json()).put("adoptExistingConfiguration", adopt)
    fun body() = SquidWire.body(json())
}
internal object SquidWire {
    const val ROOT = "/api/v1.0/gateway-proxy/squid"
    fun body(json: JSONObject): JsonBody = JsonBody().also { body -> json.keys().forEach { key -> body.raw(key, json.get(key).let { if (it is String) JSONObject.quote(it) else it.toString() }) } }
    private fun strings(json: JSONObject, key: String): List<String> = json.getJSONArray(key).let { array ->
        require(array.length() <= 4096); (0 until array.length()).map { array.getString(it) }
    }
    fun configuration(j: JSONObject): SquidConfiguration = SquidConfiguration(j.getString("bindAddress"), j.getInt("port"), j.getBoolean("allowRemoteClients"),
        strings(j, "allowedNetworks"), strings(j, "allowedDomains"), strings(j, "deniedDomains"),
        if (j.isNull("upstream")) null else j.getJSONObject("upstream").let { SquidUpstream(it.getString("host"), it.getInt("port")) })
    fun request(j: JSONObject): SquidRequest = if (j.has("action")) SquidRequest(action = SquidAction.entries.single { it.name.lowercase() == j.getString("action") })
        else SquidRequest(configuration(j.getJSONObject("configuration")), j.getBoolean("adoptExistingConfiguration"))
    fun snapshot(payload: String): SquidSnapshot = JSONObject(payload).let { root ->
        val state = root.getJSONObject("state"); val s = root.getJSONObject("status")
        SquidSnapshot(if (state.isNull("configuration")) null else configuration(state.getJSONObject("configuration")), state.getLong("desiredRevision"),
            if (state.isNull("publishedRevision")) null else state.getLong("publishedRevision"), state.optional("lastProblemCode"),
            SquidStatus(s.getBoolean("supported"), s.getBoolean("installed"), s.getBoolean("running"), s.getBoolean("enabled"), s.getBoolean("managed"),
                s.getString("configurationRevision"), s.getBoolean("packageManagementSupported"), s.getBoolean("recoveryRequired")), root.getBoolean("matchesPublishedConfiguration"))
    }
    fun preview(payload: String): SquidPreview = JSONObject(payload).let { SquidPreview(it.getBoolean("valid"), it.optional("configuration"), strings(it, "errors")) }
    fun validation(payload: String): SquidValidation = JSONObject(payload).let { SquidValidation(it.getBoolean("valid"), it.optional("problemCode"), it.getString("diagnostics")) }
    private fun JSONObject.optional(key: String): String? { require(has(key)); return if (isNull(key)) null else getString(key) }
}
