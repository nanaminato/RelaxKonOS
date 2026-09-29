package app.relaxkonos.mobile.data

import android.content.Context
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.OperationalAlert
import java.security.MessageDigest
import org.json.JSONArray
import org.json.JSONObject

/** Device-local notification preferences and seen IDs; no alert text, account name or server URL is stored. */
class AlertNotificationStore(context: Context) {
    private val preferences = context.getSharedPreferences("relaxkonos.alert-notifications", Context.MODE_PRIVATE)

    @Synchronized
    fun enabled(serviceId: String, category: AlertNotificationCategory): Boolean =
        category.name in preferences.getStringSet(policyKey(serviceId), emptySet()).orEmpty()

    @Synchronized
    fun anyEnabled(serviceId: String): Boolean =
        AlertNotificationCategory.entries.any { enabled(serviceId, it) }

    @Synchronized
    fun setEnabled(serviceId: String, category: AlertNotificationCategory, value: Boolean) {
        val key = policyKey(serviceId)
        val categories = preferences.getStringSet(key, emptySet()).orEmpty().toMutableSet()
        if (value) categories += category.name else categories -= category.name
        check(preferences.edit().putStringSet(key, categories).commit()) { "Unable to save alert notification policy." }
    }

    @Synchronized
    fun observe(owner: SessionState.Active, alerts: List<OperationalAlert>, allowed: Boolean,
        nowMillis: Long): AlertNotificationDecision {
        val key = historyKey(owner)
        val enabled = AlertNotificationCategory.entries.filterTo(mutableSetOf()) { enabled(owner.serviceId, it) }
        val decision = AlertNotificationRules.decide(readSeen(key), alerts, enabled, allowed, nowMillis)
        val rows = JSONArray()
        decision.seen.forEach { row ->
            rows.put(JSONObject().put("id", row.alertId).put("severity", row.severityRank)
                .put("status", row.status).put("count", row.occurrenceCount)
                .put("seenAt", row.seenAtMillis).put("notifiedAt", row.lastNotifiedAtMillis ?: JSONObject.NULL))
        }
        val payload = JSONObject().put("version", 1).put("seen", rows).toString()
        check(payload.length <= 64 * 1024 && preferences.edit().putString(key, payload).commit()) {
            "Unable to save alert notification history."
        }
        return decision
    }

    private fun readSeen(key: String): List<AlertNotificationSeen>? {
        val payload = preferences.getString(key, null) ?: return null
        if (payload.length > 64 * 1024) return null
        return try {
            val document = JSONObject(payload)
            if (document.getInt("version") != 1) return null
            val rows = document.getJSONArray("seen")
            if (rows.length() > 200) return null
            List(rows.length()) { index ->
                val row = rows.getJSONObject(index)
                AlertNotificationSeen(row.getString("id"), row.getInt("severity"), row.getString("status"),
                    row.getInt("count"), row.getLong("seenAt"),
                    if (row.isNull("notifiedAt")) null else row.getLong("notifiedAt"))
            }.takeIf { rows -> rows.all { AlertNotificationRules.isAlertId(it.alertId) && it.severityRank in 0..3 &&
                it.status in setOf("open", "acknowledged", "resolved", "suppressed") &&
                it.occurrenceCount >= 0 && it.seenAtMillis > 0 } }
        } catch (_: Exception) {
            null
        }
    }

    private fun policyKey(serviceId: String): String = "policy." + digest(serviceId)
    private fun historyKey(owner: SessionState.Active): String =
        "seen." + digest(owner.serviceId + "\u001f" + owner.userName)

    private fun digest(value: String): String = MessageDigest.getInstance("SHA-256")
        .digest(value.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
}
