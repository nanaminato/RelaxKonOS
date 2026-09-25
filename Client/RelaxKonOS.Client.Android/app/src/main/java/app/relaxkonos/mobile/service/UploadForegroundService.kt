package app.relaxkonos.mobile.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.text.format.Formatter
import androidx.core.app.NotificationCompat
import app.relaxkonos.mobile.MainActivity
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.data.UploadStage
import app.relaxkonos.mobile.data.UploadState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.takeWhile
import kotlinx.coroutines.launch

/**
 * Keeps one upload alive while the app is in the background, and gives the user a way to stop it.
 *
 * The transfer itself is *not* owned here. It lives in [app.relaxkonos.mobile.data.UploadCoordinator],
 * which is owned by the composition root, so it survives this service being restarted and does not
 * depend on a screen either. What the service adds is the two things only a foreground service can:
 * exemption from background execution limits for the duration, and a user-visible progress notification
 * with a cancel action — which Android 14 and later require of a long-running data-sync service anyway.
 *
 * No `WAKE_LOCK` is taken. A foreground service already keeps the app out of the background-freeze
 * state, and a vendor that freezes the network anyway is handled by the chunk retry and the
 * authoritative-offset resynchronisation, at a cost of at most one chunk.
 */
class UploadForegroundService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private var observer: Job? = null

    private val uploads get() = (application as RelaxKonApplication).container.uploads

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_CANCEL) {
            uploads.cancel()
            return START_NOT_STICKY
        }
        // `startForeground` has to happen inside the first five seconds of a foreground start, so it
        // precedes everything else — including reading the current state, which only affects the wording.
        startForegroundCompat(uploads.state.value)
        observe()
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        observer?.cancel()
        scope.cancel()
        super.onDestroy()
    }

    /**
     * Mirrors the coordinator's state into the notification, and stops the service the moment the
     * transfer stops being active. Nothing here keeps a notification alive on its own: a persistent
     * "upload finished" entry is exactly the kind of stale notification this avoids.
     */
    private fun observe() {
        if (observer != null) return
        observer = scope.launch {
            uploads.state.takeWhile { it?.isRunning == true }.collect { state ->
                notify(buildNotification(state))
            }
            stopSelf()
        }
    }

    private fun startForegroundCompat(state: UploadState?) {
        createChannel()
        val notification = buildNotification(state)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun notify(notification: Notification) {
        val manager = getSystemService(NotificationManager::class.java) ?: return
        manager.notify(NOTIFICATION_ID, notification)
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NotificationManager::class.java) ?: return
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                getString(R.string.files_upload_channel),
                // Low importance: progress is information, not an interruption, and the user is
                // already watching the transfer if they care about it.
                NotificationManager.IMPORTANCE_LOW,
            ),
        )
    }

    private fun buildNotification(state: UploadState?): Notification {
        val builder = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification_upload)
            .setContentTitle(state?.fileName ?: getString(R.string.files_upload_default_name))
            .setContentText(describe(state))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setContentIntent(openAppIntent())
            .addAction(0, getString(R.string.common_cancel), cancelIntent())
        val progress = state?.progress
        if (progress == null) {
            // Staging, committing, or an unknown length: an honest indeterminate bar rather than a
            // percentage the client cannot compute.
            builder.setProgress(0, 0, true)
        } else {
            builder.setProgress(PROGRESS_SCALE, (progress * PROGRESS_SCALE).toInt(), false)
        }
        return builder.build()
    }

    private fun describe(state: UploadState?): String {
        if (state == null) return getString(R.string.files_uploading)
        // The same wording the transfer card uses, for the same reason: while the authoritative offset is
        // being read there is no number worth showing, and the notification must not disagree with the app.
        if (state.resynchronising) return getString(R.string.files_upload_reconciling)
        return when (state.stage) {
            UploadStage.Preparing -> getString(R.string.files_upload_preparing)
            UploadStage.Committing -> getString(R.string.files_upload_committing)
            else -> {
                val total = state.totalBytes
                if (total == null) {
                    getString(R.string.files_uploading)
                } else {
                    getString(
                        R.string.files_upload_progress,
                        Formatter.formatFileSize(this, state.displayedBytes),
                        Formatter.formatFileSize(this, total),
                    )
                }
            }
        }
    }

    private fun openAppIntent(): PendingIntent = PendingIntent.getActivity(
        this,
        0,
        Intent(this, MainActivity::class.java).apply { flags = Intent.FLAG_ACTIVITY_SINGLE_TOP },
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    private fun cancelIntent(): PendingIntent = PendingIntent.getService(
        this,
        1,
        Intent(this, UploadForegroundService::class.java).setAction(ACTION_CANCEL),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    companion object {
        const val ACTION_CANCEL = "app.relaxkonos.mobile.uploads.CANCEL"

        private const val CHANNEL_ID = "uploads"
        private const val NOTIFICATION_ID = 0x5701

        /** Resolution of the notification's determinate bar. */
        private const val PROGRESS_SCALE = 1000

        /**
         * Brings the service up for one transfer.
         *
         * Called when an upload starts, not when the app is backgrounded: a foreground service that
         * appears only after the app is gone would leave the first seconds of a transfer unprotected.
         */
        fun start(context: Context) {
            val intent = Intent(context, UploadForegroundService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }
    }
}
