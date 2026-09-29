package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.OperationalAlert
import java.util.UUID

enum class AlertNotificationCategory { Deployments, Certificates, Infrastructure, Guardian }

object AlertNotificationCategories {
    fun forType(type: String): AlertNotificationCategory? = when (type) {
        "deployment.operation_failed", "docker.operation_failed" -> AlertNotificationCategory.Deployments
        "certificate.renewal_failed", "certificate.renewal_exhausted", "certificate.expiring_soon" ->
            AlertNotificationCategory.Certificates
        "docker.engine_unavailable", "tunnel.disconnected", "event-center.source_degraded" ->
            AlertNotificationCategory.Infrastructure
        "guardian.agent_unavailable", "guardian.workload_failed", "guardian.server_restart_failed" ->
            AlertNotificationCategory.Guardian
        else -> null
    }
}

data class AlertNotificationSeen(
    val alertId: String,
    val severityRank: Int,
    val status: String,
    val occurrenceCount: Int,
    val seenAtMillis: Long,
    val lastNotifiedAtMillis: Long? = null,
)

data class AlertNotificationDecision(
    val seen: List<AlertNotificationSeen>,
    val notify: List<OperationalAlert>,
    val dismiss: List<OperationalAlert>,
)

/** A first successful page becomes a quiet baseline; repeated occurrences alone never notify again. */
object AlertNotificationRules {
    private const val COOLDOWN_MILLIS = 15 * 60 * 1000L
    private const val MAX_SEEN = 200
    private const val MAX_PER_PAGE = 3
    private val ALERT_ID = Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")

    fun isAlertId(value: String): Boolean = ALERT_ID.matches(value) && UUID.fromString(value) != UUID(0, 0)

    fun decide(
        previous: List<AlertNotificationSeen>?,
        alerts: List<OperationalAlert>,
        enabled: Set<AlertNotificationCategory>,
        notificationsAllowed: Boolean,
        nowMillis: Long,
    ): AlertNotificationDecision {
        require(nowMillis > 0)
        val baseline = previous == null
        val seen = previous.orEmpty().associateBy { it.alertId }.toMutableMap()
        val notify = mutableListOf<OperationalAlert>()
        val dismiss = mutableListOf<OperationalAlert>()
        alerts.take(50).forEach { alert ->
            if (!isAlertId(alert.id)) return@forEach
            val old = seen[alert.id]
            val status = alert.status.lowercase(java.util.Locale.ROOT)
            if (status !in setOf("open", "acknowledged", "resolved", "suppressed")) return@forEach
            val rank = severityRank(alert.severity)
            val category = AlertNotificationCategories.forType(alert.type)
            val changed = old == null || (old.status == "resolved" && status == "open") || rank > old.severityRank
            val cooled = old?.lastNotifiedAtMillis == null ||
                nowMillis - old.lastNotifiedAtMillis >= COOLDOWN_MILLIS
            val deliver = !baseline && notificationsAllowed && status == "open" && rank > 0 &&
                category != null && category in enabled && changed && cooled && notify.size < MAX_PER_PAGE
            if (deliver) notify += alert
            if (old?.status == "open" && status != "open" && category != null) dismiss += alert
            seen[alert.id] = AlertNotificationSeen(alert.id, rank, status, alert.occurrenceCount.coerceAtLeast(0),
                nowMillis, if (deliver) nowMillis else old?.lastNotifiedAtMillis)
        }
        return AlertNotificationDecision(seen.values.sortedByDescending { it.seenAtMillis }.take(MAX_SEEN), notify, dismiss)
    }

    private fun severityRank(value: String): Int = when (value.lowercase(java.util.Locale.ROOT)) {
        "warning" -> 1
        "error" -> 2
        "critical" -> 3
        else -> 0
    }
}
