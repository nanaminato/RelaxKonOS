package app.relaxkonos.mobile.service

import android.app.*
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import androidx.core.app.NotificationCompat
import app.relaxkonos.mobile.MainActivity
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.SshForwardStatus
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.collect

/** User-started connected-device session. No credentials, hosts, or service URLs in its notification. */
class SshForwardForegroundService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val forwards get() = (application as RelaxKonApplication).container.sshForwards
    private var observer: Job? = null
    private var lease = -1
    override fun onBind(intent: Intent?): IBinder? = null
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == STOP) { forwards.stopAll(); stopSelf(); return START_NOT_STICKY }
        val incomingLease = intent?.getIntExtra(LEASE, -1) ?: -1
        if (incomingLease != forwards.lease) {
            if (!forwards.state.value.busy && forwards.state.value.items.none { it.status == SshForwardStatus.Running }) stopSelf(startId)
            return START_NOT_STICKY
        }
        lease = incomingLease
        try {
            getSystemService(NotificationManager::class.java)?.createNotificationChannel(
                NotificationChannel(CHANNEL, getString(R.string.ssh_forward_title), NotificationManager.IMPORTANCE_LOW))
            val notification = notification()
            startForeground(ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        } catch (_: Exception) { forwards.keeperFailed(lease); stopSelf(); return START_NOT_STICKY }
        if (observer == null) observer = scope.launch {
            forwards.state.collect { state ->
                if (!state.busy && state.items.none { it.status == SshForwardStatus.Running }) stopSelf()
                else getSystemService(NotificationManager::class.java)?.notify(ID, notification())
            }
        }
        return START_NOT_STICKY
    }
    override fun onTaskRemoved(rootIntent: Intent?) { forwards.stopAll(); stopSelf(); super.onTaskRemoved(rootIntent) }
    override fun onDestroy() {
        scope.cancel()
        if (lease == forwards.lease) forwards.stopAll()
        super.onDestroy()
    }
    private fun notification(): Notification = NotificationCompat.Builder(this, CHANNEL)
        .setSmallIcon(R.drawable.ic_notification_upload)
        .setContentTitle(getString(R.string.ssh_forward_title))
        .setContentText(getString(R.string.ssh_forward_notification))
        .setOngoing(true).setOnlyAlertOnce(true).setShowWhen(false)
        .setContentIntent(PendingIntent.getActivity(this, 23, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
        .addAction(0, getString(R.string.ssh_forward_stop_all), PendingIntent.getService(this, 24,
            Intent(this, SshForwardForegroundService::class.java).setAction(STOP), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
        .build()
    companion object {
        private const val CHANNEL = "ssh-local-forwards"
        private const val ID = 0x5703
        private const val LEASE = "lease"
        private const val STOP = "app.relaxkonos.mobile.ssh-forwards.STOP"
        fun start(context: Context, lease: Int) {
            val intent = Intent(context, SshForwardForegroundService::class.java).putExtra(LEASE, lease)
            context.startForegroundService(intent)
        }
    }
}
