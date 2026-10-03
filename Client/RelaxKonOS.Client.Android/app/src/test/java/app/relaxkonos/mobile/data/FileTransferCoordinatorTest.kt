package app.relaxkonos.mobile.data

import kotlinx.coroutines.*
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class FileTransferCoordinatorTest {
    @Test fun `session change during keeper startup prevents dispatch`() = runTest {
        var owner: Any? = Any()
        var dispatched = false
        val transfer = FileTransferCoordinator(this, { owner }, { owner = Any() }, { _, _ -> fail("Unexpected failure") })
        transfer.start(Transfer("file", TransferKind.Download)) { dispatched = true }
        runCurrent()
        assertFalse(dispatched)
        assertFalse(transfer.isRunning)
    }
    @Test fun `leaving the observing page does not stop the application transfer`() = runTest {
        val owner = Any()
        val complete = CompletableDeferred<Unit>()
        var finished = false
        val transfer = FileTransferCoordinator(this, { owner }, {}, { _, _ -> fail("Unexpected failure") })
        val page = launch { transfer.state.collect {} }
        transfer.start(Transfer("file", TransferKind.Download)) { lease ->
            complete.await()
            transfer.update(lease) { it.copy(transferredBytes = 10) }
            finished = true
        }
        runCurrent()
        page.cancel()
        runCurrent()
        assertTrue(transfer.isRunning)
        complete.complete(Unit)
        runCurrent()
        assertTrue(finished)
        assertNull(transfer.state.value)
    }

    @Test fun `late progress cleanup and old notification cancel cannot affect a newer batch`() = runTest {
        val owner = Any()
        val oldFinished = CompletableDeferred<Unit>()
        val newFinished = CompletableDeferred<Unit>()
        val transfer = FileTransferCoordinator(this, { owner }, {}, { _, _ -> fail("Unexpected failure") })
        transfer.start(Transfer("old", TransferKind.Upload)) { lease ->
            withContext(NonCancellable) {
                oldFinished.await()
                transfer.update(lease) { it.copy(label = "late old response") }
            }
        }
        runCurrent()
        val oldLease = transfer.lease
        transfer.cancel()
        transfer.start(Transfer("new", TransferKind.Download)) { newFinished.await() }
        runCurrent()
        transfer.cancel(oldLease)
        oldFinished.complete(Unit)
        runCurrent()
        assertTrue(transfer.isRunning)
        assertEquals("new", transfer.state.value?.label)
        newFinished.complete(Unit)
        runCurrent()
        assertNull(transfer.state.value)
    }

    @Test fun `changing session cancels the original task before another owner can receive progress`() = runTest {
        var owner: Any? = Any()
        var cancelled = false
        val transfer = FileTransferCoordinator(this, { owner }, {}, { _, _ -> fail("Unexpected failure") })
        transfer.start(Transfer("file", TransferKind.Download)) {
            try { awaitCancellation() } finally { cancelled = true }
        }
        runCurrent()
        owner = Any()
        transfer.sessionChanged()
        runCurrent()
        assertTrue(cancelled)
        assertFalse(transfer.isRunning)
    }

    @Test fun `foreground service failure prevents network work and is reported once`() = runTest {
        var actions = 0
        var failures = 0
        val owner = Any()
        val transfer = FileTransferCoordinator(this, { owner }, { error("service unavailable") }, { kind, _ ->
            assertEquals(TransferKind.Upload, kind)
            failures++
        })
        transfer.start(Transfer("file", TransferKind.Upload)) { actions++ }
        runCurrent()
        assertEquals(0, actions)
        assertEquals(1, failures)
        assertFalse(transfer.isRunning)
    }

    @Test fun `duplicate starts and unauthenticated starts do not dispatch a request`() = runTest {
        var owner: Any? = null
        var actions = 0
        val transfer = FileTransferCoordinator(this, { owner }, {}, { _, _ -> fail("Unexpected failure") })
        assertFalse(transfer.start(Transfer("file", TransferKind.Upload)) { actions++ })
        owner = Any()
        assertTrue(transfer.start(Transfer("file", TransferKind.Upload)) { actions++; awaitCancellation() })
        assertFalse(transfer.start(Transfer("duplicate", TransferKind.Upload)) { actions++ })
        runCurrent()
        assertEquals(1, actions)
        transfer.cancel()
        runCurrent()
    }
}
