package app.relaxkonos.mobile.ui.files

import kotlinx.coroutines.CancellationException

/** Main-dispatcher publication lifetime for one selected image request. */
internal class ImagePreviewRequest(
    private val isCurrent: () -> Boolean,
    private val onFailure: () -> Unit,
) {
    private var transferring = false

    suspend fun run(block: suspend () -> Unit) {
        try {
            block()
        } catch (cancelled: CancellationException) {
            if (isCurrent()) onFailure()
            throw cancelled
        } catch (_: Exception) {
            if (isCurrent()) onFailure()
        }
    }

    suspend fun <T> transfer(block: suspend () -> T): T {
        transferring = true
        return try {
            block()
        } finally {
            // Revoke queued progress before commit, decode or temporary-file cleanup can yield.
            transferring = false
        }
    }

    fun progress(publish: () -> Unit) {
        if (transferring && isCurrent()) publish()
    }
}
