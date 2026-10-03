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

/** Process-owned server file transfer keeper; never restarts or retries a possibly partial write. */
class FileTransferForegroundService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val files get() = (application as RelaxKonApplication).container.fileTransfers
    private var observer: Job? = null
    private var lease = -1L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == CANCEL) {
            files.cancel(intent.getLongExtra(LEASE, -1L))
            if (!files.isRunning) stopSelf(startId)
            else { lease = files.lease; observe(startId) }
            return START_NOT_STICKY
        }
        // A delayed start still must enter foreground before inspecting whether the batch finished.
        lease = files.lease
        try {
            getSystemService(NotificationManager::class.java)?.createNotificationChannel(
                NotificationChannel(CHANNEL, getString(R.string.files_transfer_channel), NotificationManager.IMPORTANCE_LOW))
            startForeground(ID, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } catch (_: Exception) {
            files.cancel(lease)
            stopSelf(startId)
            return START_NOT_STICKY
        }
        observe(startId)
        return START_NOT_STICKY
    }

    private fun observe(startId: Int) {
        observer?.cancel()
        observer = scope.launch {
            // Byte callbacks can be frequent. Update notification at most twice a second.
            coroutineScope {
                val updates = launch { observeProgress() }
                files.state.takeWhile { files.isRunning && files.lease == lease }.collect {}
                updates.cancel()
            }
            stopSelf(startId)
        }
    }

    @OptIn(FlowPreview::class)
    private suspend fun observeProgress() {
        files.state.sample(500).collect {
            if (files.isRunning) getSystemService(NotificationManager::class.java)?.notify(ID, notification())
        }
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        files.cancel(lease)
        stopSelf()
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        files.cancel(lease)
        stopSelf()
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        scope.cancel()
        files.cancel(lease)
        super.onDestroy()
    }

    private fun notification(): Notification {
        val progress = files.state.value
        val fraction = progress?.progress
        return NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification_upload)
            .setContentTitle(getString(R.string.files_transfer_channel))
            .setContentText(if (fraction == null) getString(R.string.files_transfer_running) else "${(fraction * 100).toInt()}%")
            .setProgress(1000, ((fraction ?: 0f) * 1000).toInt(), fraction == null)
            .setOngoing(true).setOnlyAlertOnce(true).setShowWhen(false)
            .setContentIntent(PendingIntent.getActivity(this, 25,
                Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
            .addAction(0, getString(R.string.common_cancel), PendingIntent.getService(this, (lease % Int.MAX_VALUE).toInt(),
                Intent(this, FileTransferForegroundService::class.java).setAction(CANCEL).putExtra(LEASE, lease),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
            .build()
    }

    companion object {
        private const val CHANNEL = "server-file-transfers"
        private const val ID = 0x5705
        private const val LEASE = "lease"
        private const val CANCEL = "app.relaxkonos.mobile.server-transfers.CANCEL"
        fun start(context: Context, lease: Long) {
            val intent = Intent(context, FileTransferForegroundService::class.java).putExtra(LEASE, lease)
            context.startForegroundService(intent)
        }
    }
}
