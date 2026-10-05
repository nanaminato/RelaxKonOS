package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

enum class TunnelProtocol(val wire: String) { Tcp("tcp"), Udp("udp"), Http("http"), Https("https"); val usesPort get() = this == Tcp || this == Udp }
enum class TunnelAuth(val wire: String) { None("none"), Token("token") }
enum class TunnelTls(val wire: String) { Default("default"), Disable("disable"), Force("force") }
enum class TunnelRuntimeMode(val wire: String) { Managed("managed"), External("external") }
enum class TunnelConnectionState(val wire: String) { SavedNotApplied("savedNotApplied"), Starting("starting"), Connected("connected"), Disconnected("disconnected"), RuntimeUnavailable("runtimeUnavailable"), Unknown("unknown") }
enum class TunnelRuntimeState(val wire: String) { NotInstalled("notInstalled"), Available("available"), Running("running"), Stopped("stopped"), ExternalInvalid("externalInvalid"), Unknown("unknown") }
data class TunnelProfile(val id: String, val name: String, val host: String, val port: Int, val auth: TunnelAuth,
    val tokenConfigured: Boolean, val tls: TunnelTls, val runtimeMode: TunnelRuntimeMode, val externalPath: String?,
    val revision: Long, val createdAtMillis: Long, val updatedAtMillis: Long, val token: String? = null)
data class TunnelDefinition(val id: String, val profileId: String, val name: String, val providerId: String, val protocol: TunnelProtocol,
    val localHost: String, val localPort: Int, val remotePort: Int?, val domain: String?, val enabled: Boolean,
    val encryption: Boolean, val compression: Boolean, val revision: Long, val createdAtMillis: Long, val updatedAtMillis: Long,
    val state: TunnelConnectionState, val problemCode: String)
data class TunnelRuntime(val runtimeId: String, val mode: TunnelRuntimeMode, val state: TunnelRuntimeState, val version: String?,
    val executablePath: String?, val problemCode: String, val startedAtMillis: Long?, val previousVersion: String?, val integrityVerified: Boolean)
data class TunnelResult(val succeeded: Boolean, val state: TunnelConnectionState, val problemCode: String)
data class TunnelRuntimeDownload(val version: String, val url: String)
data class TunnelLog(val timestampMillis: Long, val level: String, val message: String)
data class TunnelProfileRequest(val name: String, val host: String, val port: Int, val auth: TunnelAuth, val tls: TunnelTls,
    val runtimeMode: TunnelRuntimeMode, val externalPath: String?, val expectedRevision: Long?) {
    fun body() = JsonBody().string("name", name).string("host", host).raw("port", port.toString()).string("authKind", auth.wire)
        .string("tlsMode", tls.wire).string("runtimeMode", runtimeMode.wire).string("externalExecutablePath", externalPath)
        .raw("expectedRevision", expectedRevision?.toString() ?: "null")
}
data class TunnelDefinitionRequest(val profileId: String, val name: String, val protocol: TunnelProtocol, val localHost: String,
    val localPort: Int, val remotePort: Int?, val domain: String?, val enabled: Boolean, val encryption: Boolean, val compression: Boolean, val expectedRevision: Long?) {
    fun body() = JsonBody().string("serverProfileId", InstallationRoutes.canonicalId(profileId)).string("name", name).string("protocol", protocol.wire)
        .string("localHost", localHost).raw("localPort", localPort.toString()).raw("remotePort", remotePort?.toString() ?: "null")
        .string("domain", domain).bool("enabled", enabled).bool("encryption", encryption).bool("compression", compression)
        .raw("expectedRevision", expectedRevision?.toString() ?: "null")
}
object TunnelRoutes {
    const val ROOT = "/api/v1.0/tunnels"
    const val PROFILES = "$ROOT/profiles"
    const val RUNTIME = "$ROOT/runtime"
    const val DETECT = "$RUNTIME/external/detect"
    fun profile(id: String) = "$PROFILES/${InstallationRoutes.canonicalId(id)}"
    fun definition(id: String) = "$ROOT/${InstallationRoutes.canonicalId(id)}"
    fun token(id: String) = profile(id) + "/secret"
    fun apply(id: String) = profile(id) + "/apply"
    fun stop(id: String) = profile(id) + "/stop"
    fun logs(id: String) = profile(id) + "/logs"
    fun download(version: String) = "$RUNTIME/download?version=${java.net.URLEncoder.encode(version, "UTF-8")}"
}
object TunnelWire {
    private inline fun <reified T : Enum<T>> enum(value: String, wire: (T) -> String): T = enumValues<T>().single { wire(it) == value }
    private fun JSONObject.id(key: String) = InstallationRoutes.canonicalId(getString(key)).also { require(it != "00000000-0000-0000-0000-000000000000") }
    private fun JSONObject.time(key: String) = IsoInstant.requireEpochMillis(getString(key))
    private fun JSONObject.optionalTime(key: String) = if (isNull(key)) null else time(key)
    private fun JSONObject.text(key: String): String? = if (isNull(key)) null else getString(key)
    private fun JSONObject.port(key: String) = getInt(key).also { require(it in 1..65535) }
    private fun <T> records(payload: String, parse: (JSONObject) -> T) = JSONArray(payload).let { array -> List(array.length()) { parse(array.getJSONObject(it)) } }
    fun profiles(payload: String) = records(payload, ::profile).also { require(it.map { item -> item.id }.distinct().size == it.size) }
    fun profile(payload: String) = profile(JSONObject(payload))
    private fun profile(j: JSONObject) = with(j) { TunnelProfile(id("id"), getString("name"), getString("host"), port("port"),
        enum(getString("authKind"), TunnelAuth::wire), getBoolean("tokenConfigured"), enum(getString("tlsMode"), TunnelTls::wire),
        enum(getString("runtimeMode"), TunnelRuntimeMode::wire), text("externalExecutablePath"), getLong("revision").also { require(it > 0) }, time("createdAt"), time("updatedAt"), text("token")) }
    fun definitions(payload: String) = records(payload, ::definition).also { require(it.map { item -> item.id }.distinct().size == it.size) }
    fun definition(payload: String) = definition(JSONObject(payload))
    private fun definition(j: JSONObject) = with(j) { TunnelDefinition(id("id"), id("serverProfileId"), getString("name"), getString("providerId"),
        enum(getString("protocol"), TunnelProtocol::wire), getString("localHost"), port("localPort"), if (isNull("remotePort")) null else port("remotePort"),
        text("domain"), getBoolean("enabled"), getBoolean("encryption"), getBoolean("compression"), getLong("revision").also { require(it > 0) }, time("createdAt"), time("updatedAt"),
        enum(getString("state"), TunnelConnectionState::wire), getString("problemCode")) }
    fun runtime(payload: String) = with(JSONObject(payload)) { TunnelRuntime(getString("runtimeId").also { require(it == "frp") },
        enum(getString("mode"), TunnelRuntimeMode::wire), enum(getString("state"), TunnelRuntimeState::wire), text("version"), text("executablePath"), getString("problemCode"),
        optionalTime("startedAt"), text("previousVersion"), getBoolean("integrityVerified")) }
    fun result(payload: String) = with(JSONObject(payload)) { TunnelResult(getBoolean("succeeded"), enum(getString("state"), TunnelConnectionState::wire), getString("problemCode")) }
    fun download(payload: String) = with(JSONObject(payload)) { TunnelRuntimeDownload(getString("version"), getString("url")) }
    fun logs(payload: String) = records(payload) { with(it) { TunnelLog(time("timestamp"), getString("level"), getString("message")) } }
}
