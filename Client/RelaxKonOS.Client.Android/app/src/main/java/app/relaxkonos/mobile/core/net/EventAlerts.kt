package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import org.json.JSONArray
import org.json.JSONObject

data class OperationalAlert(
    val id: String, val type: String, val severity: String, val status: String,
    val problemCode: String, val occurrenceCount: Int, val lastOccurredAtMillis: Long?,
    val targetKind: String, val targetResourceId: String?, val targetOperationId: String?,
    val firstOccurredAtMillis: Long, val lastEventId: String,
    val acknowledgedAtMillis: Long?, val acknowledgedByReference: String?, val resolutionReason: String?,
)

data class OperationalAlertPage(val items: List<OperationalAlert>, val nextCursor: String?)
data class OperationalEvent(
    val id: String, val occurredAtMillis: Long, val type: String, val severity: String, val source: String, val outcome: String,
    val problemCode: String, val correlationId: String, val operationId: String?, val resourceType: String,
    val resourceReference: String, val evidence: String?, val targetKind: String, val targetResourceId: String?, val targetOperationId: String?,
)
data class OperationalEventPage(val items: List<OperationalEvent>, val nextCursor: String?)
data class OperationalAlertAction(val id: String, val kind: String, val actorReference: String?, val note: String?, val createdAtMillis: Long)
data class OperationalAlertDetail(val alert: OperationalAlert, val events: List<OperationalEvent>, val actions: List<OperationalAlertAction>)
data class EventAlertSummary(val openCount: Int, val acknowledgedCount: Int, val unacknowledgedCriticalCount: Int, val highestUnacknowledgedSeverity: String?, val updatedAtMillis: Long?)

enum class AlertStatusFilter(val wire: String) { Open("open"), Acknowledged("acknowledged"), Resolved("resolved"), Suppressed("suppressed") }
enum class EventSeverityFilter(val wire: String) { Warning("warning"), Error("error"), Critical("critical") }
enum class EventSourceFilter(val wire: String) { Deployment("deployment"), Certificate("certificate"), Guardian("guardian"), Docker("docker"), Tunnel("tunnel"), EventCenter("eventCenter") }
data class AlertQuery(val status: AlertStatusFilter? = null, val severity: EventSeverityFilter? = null)
data class EventQuery(val severity: EventSeverityFilter? = null, val source: EventSourceFilter? = null, val type: String? = null)
enum class AlertMutation { Acknowledge, Resolve, Suppress, RemoveSuppression }

object EventAlertRoutes {
    private const val ROOT = "/api/v1.0/event-alerts"
    const val ALERTS = "$ROOT/alerts"
    const val SUMMARY = "$ROOT/summary"
    fun alert(id: String): String = "$ALERTS/${encode(id)}"
    fun acknowledgement(id: String): String = "${alert(id)}/acknowledgement"
    fun resolve(id: String): String = "${alert(id)}/resolve"
    fun suppression(id: String): String = "${alert(id)}/suppression"
    fun page(cursor: String?, query: AlertQuery = AlertQuery()): String = buildString {
        append("$ALERTS?pageSize=50")
        cursor?.let { append("&cursor=${encode(it)}") }
        query.status?.let { append("&status=${it.wire}") }
        query.severity?.let { append("&severity=${it.wire}") }
    }
    fun events(cursor: String?, query: EventQuery): String = buildString {
        append("$ROOT/events?pageSize=50")
        cursor?.let { append("&cursor=${encode(it)}") }
        query.severity?.let { append("&severity=${it.wire}") }
        query.source?.let { append("&source=${it.wire}") }
        query.type?.let { append("&type=${encode(it)}") }
    }
    private fun encode(value: String): String = URLEncoder.encode(value, "UTF-8")
}

internal object EventAlertWire {
    fun page(payload: String): OperationalAlertPage = JSONObject(payload).let { json ->
        OperationalAlertPage(json.getJSONArray("items").objects(::alert), json.nullableText("nextCursor"))
    }
    fun events(payload: String): OperationalEventPage = JSONObject(payload).let { json ->
        OperationalEventPage(json.getJSONArray("items").objects(::event), json.nullableText("nextCursor"))
    }
    fun summary(payload: String): EventAlertSummary = JSONObject(payload).let { json ->
        EventAlertSummary(json.getInt("openCount").also { require(it >= 0) }, json.getInt("acknowledgedCount").also { require(it >= 0) },
            json.getInt("unacknowledgedCriticalCount").also { require(it >= 0) }, json.nullableText("highestUnacknowledgedSeverity"),
            json.nullableText("updatedAt")?.let(IsoInstant::requireEpochMillis))
    }
    fun alert(payload: String): OperationalAlert = alert(JSONObject(payload))
    fun detail(payload: String): OperationalAlertDetail = JSONObject(payload).let { json ->
        OperationalAlertDetail(alert(json.getJSONObject("alert")), json.getJSONArray("events").objects(::event),
            json.getJSONArray("actions").objects { item -> OperationalAlertAction(item.getString("actionId"), item.getString("kind"),
                item.nullableText("actorReference"), item.nullableText("note"), IsoInstant.requireEpochMillis(item.getString("createdAt"))) })
    }
    private fun event(json: JSONObject): OperationalEvent {
        val target = json.getJSONObject("remediationTarget")
        return OperationalEvent(json.getString("eventId"), IsoInstant.requireEpochMillis(json.getString("occurredAt")), json.getString("type"),
            json.getString("severity"), json.getString("source"), json.getString("outcome"), json.getString("problemCode"), json.getString("correlationId"),
            json.nullableText("operationId"), json.getString("resourceType"), json.getString("resourceReference"), json.nullableText("evidence"),
            target.getString("kind"), target.nullableText("resourceId"), target.nullableText("operationId"))
    }
    private fun alert(json: JSONObject): OperationalAlert {
        val target = json.getJSONObject("remediationTarget")
        return OperationalAlert(json.getString("alertId"), json.getString("type"), json.getString("severity"), json.getString("status"),
            json.getString("problemCode"), json.getInt("occurrenceCount").also { require(it >= 0) },
            IsoInstant.requireEpochMillis(json.getString("lastOccurredAt")), target.getString("kind"), target.nullableText("resourceId"), target.nullableText("operationId"),
            IsoInstant.requireEpochMillis(json.getString("firstOccurredAt")), json.getString("lastEventId"),
            json.nullableText("acknowledgedAt")?.let(IsoInstant::requireEpochMillis), json.nullableText("acknowledgedByReference"), json.nullableText("resolutionReason"))
    }
    private fun JSONObject.nullableText(name: String): String? {
        require(has(name)) { "Missing $name" }
        return if (isNull(name)) null else get(name) as? String ?: error("Invalid $name")
    }
    private fun <T> JSONArray.objects(parse: (JSONObject) -> T): List<T> = (0 until length()).map { parse(getJSONObject(it)) }
}
