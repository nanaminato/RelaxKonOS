package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import org.json.JSONArray
import org.json.JSONObject

enum class SmbRuntimeState(val wire: String) {
    Unsupported("unsupported"), NotInstalled("notInstalled"), Stopped("stopped"), Running("running"), Failed("failed"), Unavailable("unavailable");
    val manageable get() = this == Stopped || this == Running
}
enum class SmbAccess(val wire: String) { Read("read"), ReadWrite("readWrite") }
data class SmbStatus(val state: SmbRuntimeState, val version: String?, val serviceActive: Boolean, val port445Listening: Boolean, val healthProblemCode: String?)
data class SmbCapabilities(val supported: Boolean, val installSupported: Boolean, val sambaCredentialsSupported: Boolean,
    val managedSharesSupported: Boolean, val windowsShareSecuritySupported: Boolean, val problemCode: String?)
data class SmbPermission(val principal: String, val access: SmbAccess)
data class SmbShare(val id: String, val name: String, val path: String, val description: String?, val readOnly: Boolean,
    val enabled: Boolean, val guestAllowed: Boolean, val permissions: List<SmbPermission>, val managed: Boolean, val drifted: Boolean)
data class SmbUser(val username: String, val enabled: Boolean, val eligible: Boolean)
data class SmbConnection(val host: String, val port: Int, val windowsUncPrefix: String, val smbUriPrefix: String)
data class SmbReceipt(val operationId: String, val succeeded: Boolean, val problemCode: String?)
data class SmbFacts(val capabilities: SmbCapabilities, val status: SmbStatus, val shares: List<SmbShare>?, val users: List<SmbUser>?, val connection: SmbConnection)
data class SmbShareRequest(val name: String, val path: String, val description: String?, val readOnly: Boolean, val enabled: Boolean,
    val guestAllowed: Boolean, val permissions: List<SmbPermission>) {
    fun body() = JsonBody().string("name", name).string("path", path).apply {
        if (description == null) raw("description", "null") else string("description", description)
    }.bool("readOnly", readOnly).bool("enabled", enabled).bool("guestAllowed", guestAllowed)
        .raw("permissions", JSONArray(permissions.map { JSONObject().put("principal", it.principal).put("access", it.access.wire) }).toString())
}
enum class SmbChangeKind { Start, Stop, Restart, CreateShare, UpdateShare, DeleteShare, EnableUser, DisableUser, Password }
data class SmbChange(val kind: SmbChangeKind, val target: String? = null, val share: SmbShareRequest? = null)
object SmbRoutes {
    const val ROOT = "/api/v1.0/file-services/smb"
    fun segment(value: String): String {
        require(value.isNotBlank() && value.length <= 256 && value.none(Char::isISOControl))
        return URLEncoder.encode(value, "UTF-8").replace("+", "%20")
    }
    fun share(id: String) = "$ROOT/shares/${segment(id)}"
    fun user(username: String) = "$ROOT/users/${segment(username)}"
    fun route(change: SmbChange) = when (change.kind) {
        SmbChangeKind.Start -> "$ROOT/start"; SmbChangeKind.Stop -> "$ROOT/stop"; SmbChangeKind.Restart -> "$ROOT/restart"
        SmbChangeKind.CreateShare -> "$ROOT/shares"; SmbChangeKind.UpdateShare, SmbChangeKind.DeleteShare -> share(requireNotNull(change.target))
        SmbChangeKind.EnableUser -> user(requireNotNull(change.target)) + "/enable"
        SmbChangeKind.DisableUser -> user(requireNotNull(change.target)) + "/disable"
        SmbChangeKind.Password -> user(requireNotNull(change.target)) + "/password"
    }
    fun method(kind: SmbChangeKind) = when (kind) { SmbChangeKind.UpdateShare, SmbChangeKind.Password -> "PUT"; SmbChangeKind.DeleteShare -> "DELETE"; else -> "POST" }
}
internal object SmbWire {
    fun status(payload: String): SmbStatus = JSONObject(payload).let {
        require(it.getString("protocol") == "smb")
        SmbStatus(SmbRuntimeState.entries.single { state -> state.wire == it.getString("state") }, nullable(it, "version"),
            it.getBoolean("serviceActive"), it.getBoolean("port445Listening"), nullable(it, "healthProblemCode"))
    }
    fun capabilities(payload: String): SmbCapabilities = JSONObject(payload).let {
        SmbCapabilities(it.getBoolean("supported"), it.getBoolean("installSupported"), it.getBoolean("sambaCredentialsSupported"),
            it.getBoolean("managedSharesSupported"), it.getBoolean("windowsShareSecuritySupported"), nullable(it, "problemCode"))
    }
    fun shares(payload: String): List<SmbShare> = array(payload) { json ->
        val permissions = json.getJSONArray("permissions").also { require(it.length() <= 128) }
        SmbShare(json.getString("id").also { SmbRoutes.segment(it) }, json.getString("name"), json.getString("path"), nullable(json, "description"),
            json.getBoolean("readOnly"), json.getBoolean("enabled"), json.getBoolean("guestAllowed"), (0 until permissions.length()).map { i ->
                permissions.getJSONObject(i).let { SmbPermission(it.getString("principal"), SmbAccess.entries.single { access -> access.wire == it.getString("access") }) }
            }, json.getBoolean("managed"), json.getBoolean("drifted"))
    }.also { require(it.map(SmbShare::id).distinct().size == it.size) }
    fun users(payload: String): List<SmbUser> = array(payload) { SmbUser(it.getString("username"), it.getBoolean("enabled"), it.getBoolean("eligible")) }
        .also { require(it.map(SmbUser::username).distinct().size == it.size) }
    fun connection(payload: String): SmbConnection = JSONObject(payload).let {
        SmbConnection(it.getString("host"), it.getInt("port").also { port -> require(port in 1..65535) }, it.getString("windowsUncPrefix"), it.getString("smbUriPrefix"))
    }
    fun receipt(payload: String): SmbReceipt = JSONObject(payload).let {
        SmbReceipt(InstallationRoutes.canonicalId(it.getString("operationId")), it.getBoolean("succeeded"), nullable(it, "problemCode"))
    }
    private fun <T> array(payload: String, parse: (JSONObject) -> T): List<T> = JSONArray(payload).let { array ->
        require(array.length() <= 10_000); (0 until array.length()).map { parse(array.getJSONObject(it)) }
    }
    private fun nullable(json: JSONObject, key: String): String? { require(json.has(key)); return if (json.isNull(key)) null else json.getString(key) }
}
