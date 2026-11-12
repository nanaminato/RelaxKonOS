package app.relaxkonos.mobile.core.net

import java.net.URI
import java.time.Instant
import java.util.UUID
import org.json.JSONArray
import org.json.JSONObject

// Only public challenges cross this boundary; never provider tokens or client secrets.
class CloudAuthorizationSession(val id: String, val status: String, val authorizationUrl: String?,
    val userCode: String?, val expiresAt: Instant, val pollIntervalSeconds: Int, val connectionId: String?) {
    val pending get() = status == "pending"
    override fun toString() = "CloudAuthorizationSession(status=$status)"
}
data class CloudAuthorizationProvider(val protocol: String, val configured: Boolean)
data class CloudConnection(val id: String, val name: String, val protocol: String, val canWrite: Boolean)
object CloudAuthorizationWire {
    const val ROOT = "/api/v1.0/server/module-hosts/cloud-storage/cloud"
    fun sessionRoute(id: String) = "$ROOT/authorization/sessions/${UUID.fromString(id)}"
    private fun JSONObject.optional(key: String) = if (isNull(key)) null else optString(key).takeIf { it.isNotBlank() }
    fun session(text: String): CloudAuthorizationSession = JSONObject(text).let {
        CloudAuthorizationSession(it.getString("id"), it.getString("status"), it.optional("authorizationUrl"),
            it.optional("userCode"), Instant.parse(it.getString("expiresAt")),
            it.getInt("pollIntervalSeconds").coerceIn(1, 60), it.optional("connectionId"))
    }
    fun providers(text: String): List<CloudAuthorizationProvider> = JSONArray(text).let { a ->
        (0 until a.length()).map { a.getJSONObject(it).let { p -> CloudAuthorizationProvider(p.getString("protocol"), p.getBoolean("configured")) } }
    }
    fun connections(text: String): List<CloudConnection> = JSONArray(text).let { a ->
        (0 until a.length()).map { a.getJSONObject(it).let { p -> CloudConnection(p.getString("id"), p.getString("name"), p.getString("protocol"), p.getBoolean("canWrite")) } }
    }
    // Authorization links open only the official provider, never arbitrary schemes or embedded credentials.
    fun safeBaiduUrl(value: String): Boolean = runCatching { URI(value).let {
        it.scheme == "https" && it.host == "openapi.baidu.com" && it.rawUserInfo == null && (it.port == -1 || it.port == 443)
    } }.getOrDefault(false)
}
