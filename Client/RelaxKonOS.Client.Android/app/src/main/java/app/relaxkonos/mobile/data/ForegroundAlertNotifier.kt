package app.relaxkonos.mobile.data

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import app.relaxkonos.mobile.MainActivity
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.OperationalAlert
import app.relaxkonos.mobile.core.net.ServerCapabilities
import java.security.MessageDigest
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** Foreground-only polling. It never claims delivery while the app is stopped or the process is dead. */
class ForegroundAlertNotifier(
    private val context: Context,
    private val session: AuthSession,
    private val alerts: EventAlertRepository,
    private val store: AlertNotificationStore,
    private val scope: CoroutineScope,
) {
    private var observer: Job? = null

    fun ownerToken(owner: SessionState.Active): String = digest(owner.serviceId + "\u001f" + owner.userName)

    fun start() {
        if (observer?.isActive == true) return
        observer = scope.launch {
            var previousOwner: SessionState.Active? = null
            session.state.collectLatest { owner ->
                val previous = previousOwner
                if (previous != null && (owner !is SessionState.Active ||
                    previous.serviceId != owner.serviceId || previous.userName != owner.userName)) {
                    clearForOwner(previous)
                }
                previousOwner = owner as? SessionState.Active
                if (owner !is SessionState.Active || ServerCapabilities.EVENT_ALERTS !in owner.capabilities) {
                    return@collectLatest
                }
                while (currentCoroutineContext().isActive && session.state.value === owner) {
                    if (!store.anyEnabled(owner.serviceId)) {
                        delay(POLL_INTERVAL_MILLIS)
                        continue
                    }
                    try {
                        val result = alerts.page(owner)
                        if (result is ApiResult.Success && session.state.value === owner) {
                            val allowed = notificationsAllowed()
                            val decision = store.observe(owner, result.value.items, allowed, System.currentTimeMillis())
                            if (session.state.value === owner) {
                                decision.dismiss.forEach { dismiss(owner, it) }
                                if (allowed) decision.notify.forEach { post(owner, it) }
                            }
                        }
                    } catch (cancelled: CancellationException) {
                        throw cancelled
                    } catch (_: Exception) {
                        // A failed poll is not a verdict. The in-app list keeps its own error state.
                    }
                    delay(POLL_INTERVAL_MILLIS)
                }
            }
        }
    }

    fun stop() {
        observer?.cancel()
        observer = null
    }

    fun restart() {
        if (observer?.isActive != true) return
        stop()
        start()
    }

    fun clearForCategory(owner: SessionState.Active, category: AlertNotificationCategory) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        val prefix = notificationPrefix(owner, category)
        manager.activeNotifications.filter { it.tag?.startsWith(prefix) == true }
            .forEach { manager.cancel(it.tag, it.id) }
    }

    private fun clearForOwner(owner: SessionState.Active) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        val prefix = "rk-alert." + digest(owner.serviceId + "\u001f" + owner.userName) + "."
        manager.activeNotifications.filter { it.tag?.startsWith(prefix) == true }
            .forEach { manager.cancel(it.tag, it.id) }
    }

    private fun notificationsAllowed(): Boolean =
        (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) &&
            NotificationManagerCompat.from(context).areNotificationsEnabled()

    private fun post(owner: SessionState.Active, alert: OperationalAlert) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        if (manager.getNotificationChannel(CHANNEL_ID) == null) {
            manager.createNotificationChannel(NotificationChannel(CHANNEL_ID,
                context.getString(R.string.alert_notifications_channel), NotificationManager.IMPORTANCE_DEFAULT))
        }
        val open = Intent(context, MainActivity::class.java)
            .setAction("app.relaxkonos.mobile.OPEN_ALERT." + notificationTag(owner, alert))
            .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra(EXTRA_OWNER_TOKEN, ownerToken(owner))
        val intent = PendingIntent.getActivity(context, 0, open,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification_alert)
            .setContentTitle(context.getString(R.string.alert_notification_title))
            .setContentText(context.getString(R.string.alert_notification_body))
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setAutoCancel(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(intent)
            .build()
        manager.notify(notificationTag(owner, alert), 1, notification)
    }

    private fun dismiss(owner: SessionState.Active, alert: OperationalAlert) {
        context.getSystemService(NotificationManager::class.java)?.cancel(notificationTag(owner, alert), 1)
    }

    private fun notificationTag(owner: SessionState.Active, alert: OperationalAlert): String =
        notificationPrefix(owner, requireNotNull(AlertNotificationCategories.forType(alert.type))) + digest(alert.id)

    private fun notificationPrefix(owner: SessionState.Active, category: AlertNotificationCategory): String =
        "rk-alert." + digest(owner.serviceId + "\u001f" + owner.userName) + "." + category.name + "."

    private fun digest(value: String): String = MessageDigest.getInstance("SHA-256")
        .digest(value.toByteArray(Charsets.UTF_8)).take(16).joinToString("") { "%02x".format(it) }

    companion object {
        const val EXTRA_OWNER_TOKEN = "app.relaxkonos.mobile.alert-owner-token"
        private const val CHANNEL_ID = "relaxkonos.operation-alerts"
        private const val POLL_INTERVAL_MILLIS = 2 * 60 * 1000L
    }
}
