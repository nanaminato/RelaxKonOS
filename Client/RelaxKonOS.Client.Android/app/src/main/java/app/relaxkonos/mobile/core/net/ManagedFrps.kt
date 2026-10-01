package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

enum class ManagedFrpsState(val wire: String) { NotConfigured("notConfigured"), Stopped("stopped"), Starting("starting"), Running("running"), RuntimeUnavailable("runtimeUnavailable"), Failed("failed"), Unknown("unknown"); val active get() = this == Starting || this == Running }
data class TunnelPortRange(val start: Int, val end: Int)
data class ManagedFrps(val bindAddress: String, val bindPort: Int, val allowPorts: List<TunnelPortRange>, val httpPort: Int?, val httpsPort: Int?,
    val forceTls: Boolean, val tokenConfigured: Boolean, val dashboardEnabled: Boolean, val dashboardAddress: String, val dashboardPort: Int?,
    val dashboardUser: String?, val dashboardPasswordConfigured: Boolean, val state: ManagedFrpsState, val revision: Long,
    val appliedRevision: Long?, val problemCode: String, val startedAtMillis: Long?)
data class ManagedFrpsEditing(val configuration: ManagedFrps, val token: CharArray?) {
    override fun toString() = "ManagedFrpsEditing(revision=${configuration.revision}, token=<redacted>)"
}
data class TunnelAudit(val timestampMillis: Long, val action: String, val result: String, val problemCode: String)
data class ManagedFrpsRequest(val confirmed: Boolean, val bindAddress: String, val bindPort: Int, val allowPorts: List<TunnelPortRange>,
    val httpPort: Int?, val httpsPort: Int?, val forceTls: Boolean, val token: CharArray?, val dashboardEnabled: Boolean, val dashboardAddress: String,
    val dashboardPort: Int?, val dashboardUser: String?, val dashboardPassword: CharArray?, val expectedRevision: Long) {
    override fun toString() = "ManagedFrpsRequest(expectedRevision=$expectedRevision, credentials=<redacted>)"
    fun body() = JsonBody().bool("confirmed", confirmed).string("bindAddress", bindAddress).raw("bindPort", bindPort.toString())
        .raw("allowPorts", allowPorts.joinToString(prefix = "[", postfix = "]") { "{\"start\":${it.start},\"end\":${it.end}}" })
        .raw("vhostHttpPort", httpPort?.toString() ?: "null").raw("vhostHttpsPort", httpsPort?.toString() ?: "null").bool("forceTls", forceTls)
        .nullableSecret("token", token).bool("dashboardEnabled", dashboardEnabled).string("dashboardAddress", dashboardAddress)
        .raw("dashboardPort", dashboardPort?.toString() ?: "null").string("dashboardUser", dashboardUser).nullableSecret("dashboardPassword", dashboardPassword)
        .raw("expectedRevision", expectedRevision.toString())
}
object ManagedFrpsRoutes {
    const val ROOT = "${TunnelRoutes.ROOT}/frps"
    const val EDITOR = "$ROOT/editor"
    const val START = "$ROOT/start"
    const val STOP = "$ROOT/stop"
    const val LOGS = "$ROOT/logs"
    const val AUDIT = "$ROOT/audit"
}
object ManagedFrpsWire {
    fun configuration(payload: String) = JSONObject(payload).let { require(it.isNull("token")); configuration(it) }
    fun editing(payload: String) = JSONObject(payload).let { ManagedFrpsEditing(configuration(it), if (it.isNull("token")) null else it.getString("token").toCharArray()) }
    private fun configuration(j: JSONObject): ManagedFrps = with(j) {
        fun port(key: String): Int? = if (isNull(key)) null else getInt(key).also { require(it in 1..65535) }
        fun text(key: String): String? = if (isNull(key)) null else getString(key)
        val state = ManagedFrpsState.entries.single { it.wire == getString("state") }
        val revision = getLong("revision").also { require(if (state == ManagedFrpsState.NotConfigured) it == 0L else it > 0) }
        require(has("appliedRevision"))
        val applied = if (isNull("appliedRevision")) null else getLong("appliedRevision").also { require(it in 1..revision && state.active) }
        val ranges = getJSONArray("allowPorts").let { array -> List(array.length()) { index -> array.getJSONObject(index).let { TunnelPortRange(it.getInt("start"), it.getInt("end")) } } }
        require(ranges.size <= 64 && ranges.all { it.start in 1..65535 && it.end in it.start..65535 })
        require(state == ManagedFrpsState.NotConfigured || ranges.isNotEmpty())
        ManagedFrps(getString("bindAddress"), requireNotNull(port("bindPort")), ranges, port("vhostHttpPort"), port("vhostHttpsPort"), getBoolean("forceTls"),
            getBoolean("tokenConfigured"), getBoolean("dashboardEnabled"), getString("dashboardAddress"), port("dashboardPort"), text("dashboardUser"),
            getBoolean("dashboardPasswordConfigured"), state, revision, applied, getString("problemCode"),
            if (isNull("startedAt")) null else IsoInstant.requireEpochMillis(getString("startedAt")))
    }
    fun audit(payload: String) = JSONArray(payload).let { array -> List(array.length()) { i -> array.getJSONObject(i).let { j ->
        TunnelAudit(IsoInstant.requireEpochMillis(j.getString("timestamp")), j.getString("action"), j.getString("result"), j.getString("problemCode"))
    } }.also { require(it.size <= 200) } }
}
