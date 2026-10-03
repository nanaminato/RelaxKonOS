package app.relaxkonos.mobile.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import androidx.core.app.NotificationCompat
import app.relaxkonos.mobile.MainActivity
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.takeWhile
import kotlinx.coroutines.flow.sample
import kotlinx.coroutines.flow.collect

/** Process-owned SFTP batch keeper; never restarts or retries a possibly partial write. */
class SshTransferForegroundService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val files get() = (application as RelaxKonApplication).container.sshFiles
    private var observer: Job? = null
    private var lease = -1

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == CANCEL) {
            files.keeperStopped(intent.getIntExtra(LEASE, -1))
            if (!files.transferring) stopSelf(startId)
            return START_NOT_STICKY
        }
        // A delayed start still must enter foreground before inspecting whether the batch finished.
        lease = files.transferLease
        try {
            getSystemService(NotificationManager::class.java)?.createNotificationChannel(
                NotificationChannel(CHANNEL, getString(R.string.ssh_files_transfer_help), NotificationManager.IMPORTANCE_LOW))
            startForeground(ID, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } catch (_: Exception) {
            files.keeperStopped(lease)
            stopSelf(startId)
            return START_NOT_STICKY
        }
        observer?.cancel()
        observer = scope.launch {
            // Byte callbacks can be frequent. Update notification at most twice a second.
            coroutineScope {
                val updates = launch { observeProgress() }
                files.state.takeWhile { files.transferring && files.transferLease == lease }.collect {}
                updates.cancel()
            }
            stopSelf(startId)
        }
        return START_NOT_STICKY
    }

    @OptIn(FlowPreview::class)
    private suspend fun observeProgress() {
        files.state.sample(500).collect {
            if (files.transferring) getSystemService(NotificationManager::class.java)?.notify(ID, notification())
        }
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        files.keeperStopped(lease)
        stopSelf()
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        files.keeperStopped(lease)
        stopSelf()
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        scope.cancel()
        files.keeperStopped(lease)
        super.onDestroy()
    }

    private fun notification(): Notification {
        val progress = files.state.value.transfer
        val fraction = progress?.fraction
        return NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification_upload)
            .setContentTitle(getString(R.string.ssh_files_transfer_help))
            .setContentText(if (fraction == null) getString(R.string.ssh_files_transfer_running) else "${(fraction * 100).toInt()}%")
            .setProgress(1000, ((fraction ?: 0f) * 1000).toInt(), fraction == null)
            .setOngoing(true).setOnlyAlertOnce(true).setShowWhen(false)
            .setContentIntent(PendingIntent.getActivity(this, 25,
                Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
            .addAction(0, getString(R.string.common_cancel), PendingIntent.getService(this, lease,
                Intent(this, SshTransferForegroundService::class.java).setAction(CANCEL).putExtra(LEASE, lease),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
            .build()
    }

    companion object {
        private const val CHANNEL = "ssh-file-transfers"
        private const val ID = 0x5704
        private const val LEASE = "lease"
        private const val CANCEL = "app.relaxkonos.mobile.ssh-transfers.CANCEL"
        fun start(context: Context, lease: Int) {
            val intent = Intent(context, SshTransferForegroundService::class.java).putExtra(LEASE, lease)
            context.startForegroundService(intent)
        }
    }
}
