package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import org.json.JSONArray
import org.json.JSONObject

data class OperationalAlert(
    val id: String,
    val type: String,
    val severity: String,
    val status: String,
    val problemCode: String,
    val occurrenceCount: Int,
    val lastOccurredAtMillis: Long?,
    val targetKind: String,
    val targetResourceId: String?,
    val targetOperationId: String?,
)

data class OperationalAlertPage(val items: List<OperationalAlert>, val nextCursor: String?)
data class OperationalAlertEvent(val outcome: String, val occurredAtMillis: Long?, val problemCode: String)
data class OperationalAlertDetail(val alert: OperationalAlert, val events: List<OperationalAlertEvent>)

object EventAlertRoutes {
    private const val ROOT = "/api/v1.0/event-alerts"
    const val ALERTS = "$ROOT/alerts"
    fun alert(id: String): String = "$ALERTS/${URLEncoder.encode(id, StandardCharsets.UTF_8.name())}"
    fun acknowledgement(id: String): String = "${alert(id)}/acknowledgement"
    fun page(cursor: String?): String = if (cursor == null) "$ALERTS?pageSize=50"
        else "$ALERTS?pageSize=50&cursor=${URLEncoder.encode(cursor, StandardCharsets.UTF_8.name())}"
}

internal object EventAlertWire {
    fun page(payload: String): OperationalAlertPage = JSONObject(payload).let { json ->
        val array = json.getJSONArray("items")
        OperationalAlertPage((0 until array.length()).map { alert(array.getJSONObject(it)) }, json.optStringOrNull("nextCursor"))
    }

    fun alert(payload: String): OperationalAlert = alert(JSONObject(payload))

    fun detail(payload: String): OperationalAlertDetail = JSONObject(payload).let { json ->
        val events = json.getJSONArray("events")
        OperationalAlertDetail(alert(json.getJSONObject("alert")), (0 until events.length()).map { index ->
            val item = events.getJSONObject(index)
            OperationalAlertEvent(item.getString("outcome"), IsoInstant.toEpochMillis(item.getString("occurredAt")),
                item.getString("problemCode"))
        })
    }

    private fun alert(json: JSONObject): OperationalAlert {
        val target = json.getJSONObject("remediationTarget")
        return OperationalAlert(json.getString("alertId"), json.getString("type"), json.getString("severity"),
            json.getString("status"), json.getString("problemCode"), json.getInt("occurrenceCount"),
            IsoInstant.toEpochMillis(json.getString("lastOccurredAt")), target.getString("kind"),
            target.optStringOrNull("resourceId"), target.optStringOrNull("operationId"))
    }

    private fun JSONObject.optStringOrNull(name: String): String? =
        if (isNull(name) || !has(name)) null else getString(name)
}
