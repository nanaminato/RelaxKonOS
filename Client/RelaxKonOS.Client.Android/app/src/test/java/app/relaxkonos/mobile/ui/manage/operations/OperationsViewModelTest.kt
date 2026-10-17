package app.relaxkonos.mobile.ui.manage.operations

import android.net.Uri
import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.*
import org.junit.Assert.*

@OptIn(ExperimentalCoroutinesApi::class)
class OperationsViewModelTest {
    private val session = AuthSession(FakeGateway().apply { onLogin = { _, _, _ -> ApiResult.Success(loginSession()) } })
    private val store = ViewModelStore()
    private lateinit var model: OperationsViewModel
    private var cancelRefresh = false
    private var cancelRecovery = false
    private var cancelCancellation = false
    private var writes = 0
    private var hides = 0
    private var includeSecond = false
    private var readDiagnostics: suspend () -> ApiResult<List<String>> = { ApiResult.Success(emptyList()) }
    private var diagnosticReads = 0
    private var refreshReads = 0
    private val actions = object : OperationsActions {
        override suspend fun refresh(owner: SessionState.Active): OperationCenterSnapshot {
            refreshReads++
            if (cancelRefresh) throw CancellationException("private refresh")
            return OperationCenterSnapshot(listOfNotNull(item(owner), if (includeSecond) otherItem(owner) else null), false)
        }
        override suspend fun diagnostics(owner: SessionState.Active, item: ObservedOperation): ApiResult<List<String>> {
            diagnosticReads++; return readDiagnostics()
        }
        override suspend fun cancel(owner: SessionState.Active, item: ObservedOperation): ApiResult<*> {
            writes++
            if (cancelCancellation) throw CancellationException("private cancel")
            return ApiResult.Success(Unit)
        }
        override suspend fun hide(owner: SessionState.Active, item: ObservedOperation) { hides++ }
        override suspend fun recoverInstallation(owner: SessionState.Active, id: String): ApiResult<*> {
            if (cancelRecovery) throw CancellationException("private recovery")
            return ApiResult.Success(Unit)
        }
        override suspend fun exportReport(uri: Uri, report: String) = true
    }
    @Before fun setup() { Dispatchers.setMain(StandardTestDispatcher()) }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun TestScope.start(): SessionState.Active {
        session.login(ServerConnectionIdentityRules.direct("https://operations-review.invalid"), "review", "test".toCharArray()) {}
        model = OperationsViewModel(session, actions, StandardTestDispatcher(testScheduler))
        store.put("operations", model)
        val owner = session.state.value as SessionState.Active
        model.refresh(owner); advanceUntilIdle()
        return owner
    }
    private fun item(owner: SessionState.Active) = ObservedOperation(
        OperationReference(owner.serviceId, owner.userName, OperationDomain.Compose, "certificate", "operation", 1),
        OperationCheck.Verified, "certificate", state = "running", cancellable = true)
    private fun otherItem(owner: SessionState.Active) = item(owner).let { it.copy(reference = it.reference.copy(operationId = "other")) }
    @Test fun `same account relogin clears old task facts and approval before page refresh`() = runTest {
        val owner = start(); model.select(owner, item(owner)); advanceUntilIdle(); model.requestHide(owner, item(owner))
        session.login(ServerConnectionIdentityRules.direct("https://operations-review.invalid"), "review", "test".toCharArray()) {}
        runCurrent()
        assertNull(model.state.owner); assertTrue(model.state.items.isEmpty())
        assertNull(model.state.selectedKey); assertNull(model.state.diagnostics); assertNull(model.state.hideReview)
        val next = session.state.value as SessionState.Active
        model.refresh(next); advanceUntilIdle()
        assertSame(next, model.state.owner); assertFalse(model.state.items.isEmpty()); assertEquals(0, writes)
    }
    @Test fun `logout clears old selection diagnostics and cancellation approval`() = runTest {
        val owner = start(); model.select(owner, item(owner)); advanceUntilIdle(); model.requestCancel(owner, item(owner))
        session.clearSession(); runCurrent()
        assertNull(model.state.owner); assertTrue(model.state.items.isEmpty())
        assertNull(model.state.selectedKey); assertNull(model.state.diagnostics); assertNull(model.state.cancelReview)
        assertFalse(model.state.loading); assertFalse(model.state.cancelling); assertEquals(0, writes)
    }
    @Test fun `changing selection withdraws hide approval`() = runTest {
        includeSecond = true
        val owner = start(); model.select(owner, item(owner)); runCurrent(); model.requestHide(owner, item(owner))
        val review = requireNotNull(model.state.hideReview)
        model.select(owner, otherItem(owner)); runCurrent()
        model.hide(owner, review); advanceUntilIdle()
        assertEquals(0, hides)
    }
    @Test fun `refresh withdraws old cancellation approval`() = runTest {
        val owner = start(); model.select(owner, item(owner)); runCurrent(); model.requestCancel(owner, item(owner))
        val review = requireNotNull(model.state.cancelReview)
        model.refresh(owner); advanceUntilIdle()
        model.cancel(owner, review); advanceUntilIdle(); assertEquals(0, writes)
    }
    @Test fun `dismissed hide approval cannot be executed`() = runTest {
        val owner = start(); model.select(owner, item(owner)); runCurrent(); model.requestHide(owner, item(owner))
        val review = requireNotNull(model.state.hideReview); model.dismissHide()
        model.hide(owner, review); advanceUntilIdle(); assertEquals(0, hides)
    }
    @Test fun `cancellation approval can only be consumed once`() = runTest {
        val owner = start(); model.select(owner, item(owner)); runCurrent(); model.requestCancel(owner, item(owner))
        val review = requireNotNull(model.state.cancelReview)
        model.cancel(owner, review); model.cancel(owner, review); advanceUntilIdle()
        assertEquals(1, writes)
    }
    @Test fun `reopened confirmation cannot consume old approval for identical task`() = runTest {
        val owner = start(); model.select(owner, item(owner)); runCurrent(); model.requestCancel(owner, item(owner))
        val old = requireNotNull(model.state.cancelReview); model.dismissCancel()
        model.requestCancel(owner, item(owner)); val fresh = requireNotNull(model.state.cancelReview)
        model.cancel(owner, old); advanceUntilIdle(); assertEquals(0, writes)
        model.cancel(owner, fresh); advanceUntilIdle(); assertEquals(1, writes)
    }
    @Test fun `confirmed hide consumes approval once and removes only its target`() = runTest {
        includeSecond = true
        val owner = start(); model.select(owner, item(owner)); runCurrent(); model.requestHide(owner, item(owner))
        val review = requireNotNull(model.state.hideReview)
        model.hide(owner, review); model.hide(owner, review); advanceUntilIdle()
        assertEquals(1, hides); assertFalse(model.state.hiding)
        assertEquals(listOf(otherItem(owner)), model.state.items); assertEquals(0, writes)
    }
    @Test fun `observation rechecks foreground after waiting and resumes with fresh interval`() = runTest {
        val owner = start()
        var epoch = 0
        val initial = epoch
        val old = backgroundScope.launch { model.observe(owner) { epoch == initial } }
        runCurrent(); advanceTimeBy(4999); epoch += 2
        advanceTimeBy(1); runCurrent()
        assertEquals(1, refreshReads); assertTrue(old.isCompleted)
        val resumed = epoch
        val next = backgroundScope.launch { model.observe(owner) { epoch == resumed } }
        runCurrent(); advanceTimeBy(4999); runCurrent(); assertEquals(1, refreshReads)
        advanceTimeBy(1); runCurrent(); assertEquals(2, refreshReads)
        next.cancel(); assertEquals(0, writes)
    }
    @Test fun `quiet task refresh preserves selected diagnostic result`() = runTest {
        val owner = start()
        readDiagnostics = { ApiResult.Success(listOf("diagnostic snapshot")) }
        model.select(owner, item(owner)); advanceUntilIdle()
        model.poll(owner); advanceUntilIdle()
        assertEquals(ApiResult.Success(listOf("diagnostic snapshot")), model.state.diagnostics)
        assertEquals(1, diagnosticReads)
    }
    @Test fun `quiet task refresh does not invalidate ongoing diagnostic read`() = runTest {
        val owner = start(); val gate = CompletableDeferred<ApiResult<List<String>>>()
        readDiagnostics = { gate.await() }
        try {
            model.select(owner, item(owner)); runCurrent()
            model.poll(owner); runCurrent()
            assertTrue(model.state.diagnosticsLoading)
            gate.complete(ApiResult.Success(listOf("diagnostic snapshot"))); advanceUntilIdle()
            assertEquals(ApiResult.Success(listOf("diagnostic snapshot")), model.state.diagnostics)
            assertFalse(model.state.diagnosticsLoading)
        } finally { gate.complete(ApiResult.Transport(null)) }
    }
    @Test fun `explicit task refresh reloads selected diagnostics`() = runTest {
        val owner = start()
        readDiagnostics = { ApiResult.Success(listOf("diagnostic-$diagnosticReads")) }
        model.select(owner, item(owner)); advanceUntilIdle()
        model.refresh(owner); advanceUntilIdle()
        assertEquals(2, diagnosticReads)
        assertEquals(ApiResult.Success(listOf("diagnostic-2")), model.state.diagnostics)
    }
    @Test fun `diagnostic exception is safe failure and explicit reselect recovers`() = runTest {
        val owner = start()
        readDiagnostics = { throw IllegalStateException("private diagnostic detail") }
        model.select(owner, item(owner)); advanceUntilIdle()
        assertEquals(ApiResult.Transport(null), model.state.diagnostics)
        assertFalse(model.state.diagnosticsLoading)
        readDiagnostics = { ApiResult.Success(listOf("current diagnostic")) }
        model.select(owner, item(owner)); advanceUntilIdle()
        assertEquals(ApiResult.Success(listOf("current diagnostic")), model.state.diagnostics)
        assertFalse(model.state.diagnosticsLoading)
        assertEquals(0, writes)
    }
    @Test fun `cancelled diagnostic read becomes unavailable without writing`() = runTest {
        val owner = start()
        readDiagnostics = { throw CancellationException("private diagnostic detail") }
        model.select(owner, item(owner)); advanceUntilIdle()
        assertEquals(ApiResult.Transport(null), model.state.diagnostics); assertEquals(0, writes)
        assertFalse(model.state.diagnosticsLoading)
    }
    @Test fun `switching away and back rejects old diagnostic response`() = runTest {
        includeSecond = true
        val owner = start(); val old = CompletableDeferred<ApiResult<List<String>>>()
        var reads = 0
        readDiagnostics = { if (++reads == 1) old.await() else ApiResult.Success(listOf("new diagnostic")) }
        try {
            model.select(owner, item(owner)); runCurrent()
            model.select(owner, otherItem(owner)); runCurrent()
            model.select(owner, item(owner)); runCurrent()
            old.complete(ApiResult.Success(listOf("old diagnostic"))); advanceUntilIdle()
            assertEquals(ApiResult.Success(listOf("new diagnostic")), model.state.diagnostics)
        } finally { old.complete(ApiResult.Transport(null)) }
    }
    @Test fun `late diagnostic exception cannot replace current selection`() = runTest {
        includeSecond = true
        val owner = start(); val old = CompletableDeferred<Unit>()
        var reads = 0
        readDiagnostics = {
            if (++reads == 1) { old.await(); throw IllegalStateException("private old detail") }
            ApiResult.Success(listOf("new diagnostic"))
        }
        try {
            model.select(owner, item(owner)); runCurrent()
            model.select(owner, otherItem(owner)); runCurrent()
            model.select(owner, item(owner)); runCurrent()
            old.complete(Unit); advanceUntilIdle()
            assertEquals(ApiResult.Success(listOf("new diagnostic")), model.state.diagnostics)
        } finally { old.complete(Unit) }
    }
    @Test fun `cancelled refresh ends loading and explicit read can recover`() = runTest {
        val owner = start(); cancelRefresh = true
        model.refresh(owner); advanceUntilIdle()
        assertFalse(model.state.loading); assertTrue(model.state.error)
        cancelRefresh = false; model.refresh(owner); advanceUntilIdle()
        assertFalse(model.state.loading); assertFalse(model.state.error); assertEquals(0, writes)
    }
    @Test fun `cancelled installation lookup ends loading without writing`() = runTest {
        val owner = start(); cancelRecovery = true
        model.recoverInstallation(owner, "operation"); advanceUntilIdle()
        assertFalse(model.state.loading); assertTrue(model.state.error); assertEquals(0, writes)
    }
    @Test fun `cancelled cancellation ends busy and refresh never resends it`() = runTest {
        val owner = start(); cancelCancellation = true
        model.select(owner, item(owner)); runCurrent(); model.requestCancel(owner, item(owner))
        val review = requireNotNull(model.state.cancelReview)
        model.cancel(owner, review); advanceUntilIdle()
        assertFalse(model.state.cancelling); assertTrue(model.state.error); assertEquals(1, writes)
        assertEquals(OperationCheck.Unavailable, model.state.items.single().check)
        model.cancel(owner, review); advanceUntilIdle(); assertEquals(1, writes)
        model.refresh(owner); advanceUntilIdle(); assertEquals(1, writes)
    }
    @Test fun `old login cannot cancel after same account signs in again`() = runTest {
        val owner = start()
        model.select(owner, item(owner)); runCurrent(); model.requestCancel(owner, item(owner))
        val review = requireNotNull(model.state.cancelReview)
        session.login(ServerConnectionIdentityRules.direct("https://operations-review.invalid"), "review", "test".toCharArray()) {}
        model.cancel(owner, review); advanceUntilIdle()
        assertEquals(0, writes)
    }
}
