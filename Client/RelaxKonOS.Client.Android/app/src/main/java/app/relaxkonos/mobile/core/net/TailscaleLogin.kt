package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import java.time.Instant

enum class TailscaleLoginState { Prepared, Applying, Issued, Completed, ApprovalRequired, Unknown, Rejected }
data class TailscaleLoginObservation(val revision: String, val preferencesRevision: String, val backendState: String, val canStart: Boolean)
data class TailscaleLoginIdentity(val operationId: String, val hostId: String, val actor: String, val sessionId: String,
    val expectedRevision: String, val expiresAt: Instant)
data class TailscaleLoginClosure(val intent: TailscaleLoginIdentity, val observation: TailscaleLoginObservation, val closedAt: Instant)
data class TailscaleLoginOperation(val operationId: String, val state: TailscaleLoginState, val expectedRevision: String,
    val expiresAt: Instant, val updatedAt: Instant, val problemCode: String?, val closure: TailscaleLoginClosure?)
data class TailscaleLoginRecovery(val operationId: String, val observation: TailscaleLoginObservation?, val canClose: Boolean,
    val expiresAt: Instant?, val closure: TailscaleLoginClosure?, val problemCode: String?)
class TailscaleLoginChallenge(val operationId: String, val authenticationUrl: String, val expiresAt: Instant) {
    override fun toString() = "TailscaleLoginChallenge(operationId=$operationId, authenticationUrl=<redacted>)"
}
object TailscaleLoginWire {
    const val OPERATIONS = "/api/v1.0/tailscale/login/operations"
    fun route(id: String): String { TailscaleWire.id(id); return "$OPERATIONS/$id" }
    fun officialUrl(value: String): Boolean {
        val prefix = "https://login.tailscale.com/a/"
        if (!value.startsWith(prefix)) return false
        return value.substring(prefix.length).matches(Regex("[A-Za-z0-9_-]{10,256}"))
    }
    private fun text(root: JSONObject, key: String) = if (root.isNull(key)) null else root.getString(key)
    private fun obj(root: JSONObject, key: String) = if (root.isNull(key)) null else root.getJSONObject(key)
    private fun flag(root: JSONObject, key: String) = root.get(key) as? Boolean ?: error("Invalid Tailscale boolean")
    private fun observation(root: JSONObject) = TailscaleLoginObservation(root.getString("revision").also(TailscaleWire::revision),
        root.getString("preferencesRevision").also(TailscaleWire::revision),root.getString("backendState"),flag(root,"canStart")).also {
            require(it.backendState in setOf("NeedsLogin","NeedsMachineAuth","Running","Stopped"))
            require(!it.canStart || it.backendState == "NeedsLogin")
        }
    private fun identity(root: JSONObject) = TailscaleLoginIdentity(root.getString("operationId").also(TailscaleWire::id),
        root.getString("hostId").also(TailscaleWire::id),root.getString("actor").also(TailscaleWire::id),root.getString("sessionId").also(TailscaleWire::id),
        root.getString("expectedRevision").also(TailscaleWire::revision),Instant.parse(root.getString("expiresAt")))
    private fun closure(root: JSONObject) = TailscaleLoginClosure(identity(root.getJSONObject("intent")),observation(root.getJSONObject("observation")),Instant.parse(root.getString("closedAt")))
    fun operation(raw: String): TailscaleLoginOperation {
        val root = JSONObject(raw)
        return TailscaleLoginOperation(root.getString("operationId").also(TailscaleWire::id),TailscaleLoginState.valueOf(root.getString("state")),
            root.getString("expectedRevision").also(TailscaleWire::revision),Instant.parse(root.getString("expiresAt")),Instant.parse(root.getString("updatedAt")),
            text(root,"problemCode"),obj(root,"closure")?.let(::closure)).also { operation ->
                operation.closure?.let { closed ->
                    require(operation.state in setOf(TailscaleLoginState.Issued,TailscaleLoginState.Unknown,TailscaleLoginState.ApprovalRequired))
                    require(closed.intent.operationId == operation.operationId && closed.intent.expectedRevision == operation.expectedRevision && closed.intent.expiresAt == operation.expiresAt)
                }
            }
    }
    fun recovery(raw: String): TailscaleLoginRecovery {
        val root = JSONObject(raw)
        return TailscaleLoginRecovery(root.getString("operationId").also(TailscaleWire::id),obj(root,"observation")?.let(::observation),flag(root,"canClose"),
            text(root,"expiresAt")?.let(Instant::parse),obj(root,"closure")?.let(::closure),text(root,"problemCode")).also {
                require(!it.canClose || it.observation != null && it.expiresAt != null && it.closure == null)
                require(it.closure == null || it.closure.intent.operationId == it.operationId && !it.canClose)
            }
    }
    fun challenge(raw: String): TailscaleLoginChallenge {
        val root = JSONObject(raw)
        return TailscaleLoginChallenge(root.getString("operationId").also(TailscaleWire::id),root.getString("authenticationUrl").also { require(officialUrl(it)) },Instant.parse(root.getString("expiresAt")))
    }
}
