package app.relaxkonos.mobile.ui.files

import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ImagePreviewRequestTest {
    @Test fun `provider cancellation reports failure propagates and requires explicit retry`() = runTest {
        var state = "loading"
        var reads = 0
        val request = ImagePreviewRequest({ true }, { state = "retry" })
        val job = launch {
            request.run { reads++; throw CancellationException("private provider detail") }
        }
        runCurrent()
        assertTrue(job.isCancelled)
        assertEquals("retry", state)
        assertEquals(1, reads)
        runCurrent()
        assertEquals(1, reads)
        ImagePreviewRequest({ true }, { state = "retry" }).run { reads++; state = "ready" }
        assertEquals(2, reads)
        assertEquals("ready", state)
    }

    @Test fun `queued progress cannot replace completion while decode suspends`() = runTest {
        var state = "loading"
        val decode = CompletableDeferred<Unit>()
        val request = ImagePreviewRequest({ true }, { state = "retry" })
        val job = launch {
            request.run {
                request.transfer {
                    request.progress { state = "progress" }
                    launch { request.progress { state = "late progress" } }
                }
                state = "decoding"
                decode.await()
                state = "ready"
            }
        }
        runCurrent()
        assertEquals("decoding", state)
        decode.complete(Unit)
        runCurrent()
        assertTrue(job.isCompleted)
        assertEquals("ready", state)
    }

    @Test fun `failed or cancelled transfer revokes progress before cleanup yields`() = runTest {
        for (cancel in listOf(false, true)) {
            var state = "loading"
            val cleanup = CompletableDeferred<Unit>()
            val request = ImagePreviewRequest({ true }, { state = "retry" })
            val job = launch {
                request.run {
                    try {
                        request.transfer<Unit> {
                            launch { request.progress { state = "late progress" } }
                            if (cancel) throw CancellationException("private detail")
                            else error("private detail")
                        }
                    } finally {
                        withContext(NonCancellable) { cleanup.await() }
                    }
                }
            }
            runCurrent()
            assertEquals("loading", state)
            cleanup.complete(Unit)
            runCurrent()
            assertEquals("retry", state)
            assertEquals(cancel, job.isCancelled)
            request.progress { state = "late progress" }
            assertEquals("retry", state)
        }
    }

    @Test fun `selection or owner revocation rejects old failure and progress`() = runTest {
        var current = true
        var state = "old loading"
        val pending = CompletableDeferred<Unit>()
        val request = ImagePreviewRequest({ current }, { state = "old failure" })
        val job = launch {
            request.run {
                request.transfer {
                    pending.await()
                    request.progress { state = "old progress" }
                    throw CancellationException()
                }
            }
        }
        runCurrent()
        current = false
        state = "new image"
        pending.complete(Unit)
        runCurrent()
        assertTrue(job.isCancelled)
        assertEquals("new image", state)
    }
}
