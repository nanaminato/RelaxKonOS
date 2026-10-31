package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import java.time.Instant
import java.util.UUID

data class TailscaleConfiguration(val acceptDns: Boolean, val acceptRoutes: Boolean) {
    fun json() = JSONObject().put("acceptDns", acceptDns).put("acceptRoutes", acceptRoutes)
}
/** The official client's closed preference set; arbitrary flags are never accepted. */
data class TailscalePreferences(val acceptDns: Boolean, val acceptRoutes: Boolean, val shieldsUp: Boolean,
    val exitNode: String?, val exitNodeAllowLanAccess: Boolean, val unattended: Boolean,
    val autoUpdate: Boolean, val advertiseExitNode: Boolean) {
    fun json(): JSONObject = JSONObject()
        .put("acceptDns", acceptDns).put("acceptRoutes", acceptRoutes).put("shieldsUp", shieldsUp)
        .put("exitNode", exitNode ?: JSONObject.NULL).put("exitNodeAllowLanAccess", exitNodeAllowLanAccess)
        .put("unattended", unattended).put("autoUpdate", autoUpdate).put("advertiseExitNode", advertiseExitNode)
    companion object {
        fun of(configuration: TailscaleConfiguration) = TailscalePreferences(configuration.acceptDns,
            configuration.acceptRoutes, false, null, false, false, false, false)
    }
}
data class TailscaleControl(val revision: String, val connected: Boolean, val authenticated: Boolean,
    val configuration: TailscaleConfiguration, val canControl: Boolean, val problemCode: String?,
    val preferences: TailscalePreferences? = null, val platform: String? = null)
enum class TailscaleAction { Connect, Disconnect, Configure, ConfigurePreferences, Logout }
enum class TailscaleOperationState { Prepared, Applying, Applied, Rejected, Unknown }
data class TailscalePlanRequest(val operationId: String, val expectedRevision: String, val action: TailscaleAction,
    val configuration: TailscaleConfiguration? = null, val preferences: TailscalePreferences? = null) {
    fun body(): JsonBody {
        TailscaleWire.id(operationId); TailscaleWire.revision(expectedRevision)
        require((action == TailscaleAction.Configure) == (configuration != null))
        require((action == TailscaleAction.ConfigurePreferences) == (preferences != null))
        require(configuration == null || action == TailscaleAction.Configure)
        require(preferences == null || action == TailscaleAction.ConfigurePreferences)
        return JsonBody().string("operationId", operationId).string("expectedRevision", expectedRevision).string("action", action.name)
            .also { body ->
                if (configuration != null) body.raw("configuration", configuration.json().toString())
                if (preferences != null) body.raw("preferences", preferences.json().toString())
            }
    }
}
data class TailscaleClosure(val operationId: String, val observation: TailscaleControl, val closedAt: Instant, val actor: String)
data class TailscaleOperation(val operationId: String, val action: TailscaleAction, val state: TailscaleOperationState,
    val expectedRevision: String, val expiresAt: Instant, val updatedAt: Instant, val configuration: TailscaleConfiguration?,
    val problemCode: String?, val closure: TailscaleClosure?, val preferences: TailscalePreferences? = null)
data class TailscaleRecovery(val operationId: String, val observation: TailscaleControl?, val canClose: Boolean,
    val expiresAt: Instant?, val closure: TailscaleClosure?, val problemCode: String?)

data class TailscaleAccount(val loginName: String, val displayName: String)
data class TailscalePeer(val id: String, val hostName: String, val dnsName: String,
    val addresses: List<String>, val online: Boolean, val exitNode: Boolean, val os: String? = null,
    val tags: List<String> = emptyList(), val exitNodeOption: Boolean = false, val expired: Boolean = false,
    val keyExpiry: Instant? = null, val lastSeen: Instant? = null, val owner: String? = null)
data class TailscaleStatus(val state: String, val version: String?, val backendState: String?,
    val self: TailscalePeer?, val peers: List<TailscalePeer>, val peersRestricted: Boolean,
    val tailnetName: String? = null, val magicDnsSuffix: String? = null,
    val account: TailscaleAccount? = null, val exitNode: TailscalePeer? = null)

object TailscaleWire {
    const val ROOT = "/api/v1.0/tailscale"
    const val STATUS = "/api/v1.0/tailscale/status"
    fun id(value: String) { require(UUID.fromString(value).toString() == value && value != "00000000-0000-0000-0000-000000000000") }
    fun revision(value: String) { require(value.matches(Regex("[a-fA-F0-9]{64}"))) }
    fun operationRoute(value: String): String { id(value); return "$ROOT/operations/$value" }
    private fun flag(root: JSONObject, name: String) = (root.get(name) as? Boolean) ?: error("Invalid Tailscale boolean")
    private fun configuration(root: JSONObject) = TailscaleConfiguration(flag(root,"acceptDns"), flag(root,"acceptRoutes"))
    private fun preferences(root: JSONObject): TailscalePreferences {
        val exitNode = if (!root.has("exitNode") || root.isNull("exitNode")) null else root.getString("exitNode").takeIf { it.isNotEmpty() }
        return TailscalePreferences(flag(root,"acceptDns"), flag(root,"acceptRoutes"), flag(root,"shieldsUp"), exitNode,
            flag(root,"exitNodeAllowLanAccess"), flag(root,"unattended"), flag(root,"autoUpdate"), flag(root,"advertiseExitNode"))
    }
    private fun optional(root: JSONObject, key: String): String? =
        if (!root.has(key) || root.isNull(key)) null else root.getString(key).takeIf { it.isNotEmpty() }
    private fun optionalInstant(root: JSONObject, key: String): Instant? = optional(root,key)?.let(Instant::parse)
    private fun objectOrNull(root: JSONObject, key: String) =
        if (!root.has(key) || root.isNull(key)) null else root.getJSONObject(key)
    private fun tags(value: JSONObject): List<String> {
        if (!value.has("tags") || value.isNull("tags")) return emptyList()
        val values = value.getJSONArray("tags")
        return List(values.length()) { values.getString(it) }
    }
    private fun peer(value: JSONObject): TailscalePeer {
        val addresses = value.getJSONArray("addresses")
        return TailscalePeer(value.getString("id"), value.getString("hostName"), value.getString("dnsName"),
            List(addresses.length()) { addresses.getString(it) }, flag(value,"online"), flag(value,"exitNode"),
            optional(value,"os"), tags(value), value.optBoolean("exitNodeOption", false), value.optBoolean("expired", false),
            optionalInstant(value,"keyExpiry"), optionalInstant(value,"lastSeen"), optional(value,"owner"))
    }
    private fun account(root: JSONObject) = TailscaleAccount(root.getString("loginName"), root.getString("displayName"))
    private fun control(root: JSONObject): TailscaleControl = TailscaleControl(root.getString("revision").also(::revision),
        flag(root,"connected"), flag(root,"authenticated"), configuration(root.getJSONObject("configuration")),
        flag(root,"canControl"), optional(root, "problemCode"),
        objectOrNull(root,"preferences")?.let(::preferences), optional(root,"platform"))
    fun control(raw: String) = control(JSONObject(raw))
    private fun closure(root: JSONObject) = TailscaleClosure(root.getString("operationId").also(::id),
        control(root.getJSONObject("observation")), Instant.parse(root.getString("closedAt")), root.getString("actor").also(::id))
    fun operation(raw: String): TailscaleOperation {
        val root = JSONObject(raw)
        val result = TailscaleOperation(root.getString("operationId").also(::id), TailscaleAction.valueOf(root.getString("action")),
            TailscaleOperationState.valueOf(root.getString("state")), root.getString("expectedRevision").also(::revision),
            Instant.parse(root.getString("expiresAt")), Instant.parse(root.getString("updatedAt")),
            objectOrNull(root,"configuration")?.let(::configuration), optional(root,"problemCode"),
            objectOrNull(root,"closure")?.let(::closure), objectOrNull(root,"preferences")?.let(::preferences))
        require((result.action == TailscaleAction.Configure) == (result.configuration != null))
        require((result.action == TailscaleAction.ConfigurePreferences) == (result.preferences != null))
        require(result.configuration == null || result.action == TailscaleAction.Configure)
        require(result.preferences == null || result.action == TailscaleAction.ConfigurePreferences)
        result.closure?.let { require(result.state == TailscaleOperationState.Unknown && it.operationId == result.operationId) }
        return result
    }
    fun recovery(raw: String): TailscaleRecovery {
        val root = JSONObject(raw)
        return TailscaleRecovery(root.getString("operationId").also(::id), objectOrNull(root,"observation")?.let(::control),
            flag(root,"canClose"), optional(root,"expiresAt")?.let(Instant::parse), objectOrNull(root,"closure")?.let(::closure), optional(root,"problemCode"))
            .also { value ->
                require(!value.canClose || value.observation != null && value.expiresAt != null && value.closure == null)
                value.closure?.let { require(it.operationId == value.operationId && !value.canClose) }
            }
    }
    fun status(raw: String): TailscaleStatus {
        val root = JSONObject(raw)
        val state = root.getString("state")
        require(state in setOf("observed", "not-installed", "unavailable", "unsupported", "timeout", "invalid-response"))
        val restricted = flag(root,"peersRestricted")
        val peers = if (restricted || !root.has("peers") || root.isNull("peers")) null else root.getJSONArray("peers")
        return TailscaleStatus(state, optional(root,"version"), optional(root,"backendState"),
            objectOrNull(root,"self")?.let(::peer),
            if (peers == null) emptyList() else List(peers.length()) { peer(peers.getJSONObject(it)) }, restricted,
            optional(root,"tailnetName"), optional(root,"magicDnsSuffix"),
            objectOrNull(root,"account")?.let(::account), objectOrNull(root,"exitNode")?.let(::peer))
    }
}
