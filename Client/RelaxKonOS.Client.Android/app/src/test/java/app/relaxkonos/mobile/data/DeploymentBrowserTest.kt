package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class DeploymentBrowserTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val repository = DeploymentRepository(gateway, session)
    private val capabilities = setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS, ServerCapabilities.DOCKER)
    private val runtime = DeploymentRuntime(true, "", "28", "linux", "amd64")
    private val app = DeploymentApplication("id-1", "first", "image", "web", "running", "unknown", "http",
        null, null, 80, null, "127.0.0.1", null, null, null, null, null, "/", DeploymentLimits(null, null, null), emptyList(), emptyList(), "2026-10-01T00:00:00Z")

    private suspend fun signIn(user: String = "nana", caps: Set<String> = capabilities, url: String = "https://server.local") {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession(userName = user, capabilities = caps)) }
        gateway.onDeploymentTemplates = { _, _ -> ApiResult.Success(emptyList()) }
        session.login(ServerConnectionIdentityRules.direct(url), user, charArrayOf('p')) {}
    }

    private fun reads() {
        gateway.onDeploymentRuntime = { _, _ -> ApiResult.Success(runtime) }
        gateway.onDeploymentTemplates = { _, _ -> ApiResult.Success(emptyList()) }
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Success(listOf(app)) }
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(app, emptyList(), emptyList(), null)) }
    }

    @Test fun `absent deployment capability performs no network reads`() = runTest {
        signIn(caps = emptySet())
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        assertNull(browser.state.value.applications)
        assertFalse(browser.state.value.loading)
    }

    @Test fun `missing Docker capability does not prevent reading deployment records`() = runTest {
        signIn(caps = setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS))
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Success(listOf(app)) }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        assertNull(browser.state.value.runtime)
        assertEquals(listOf(app), (browser.state.value.applications as ApiResult.Success).value)
    }

    @Test fun `engine failure does not hide drift-aware application records`() = runTest {
        signIn(); reads()
        gateway.onDeploymentRuntime = { _, _ -> ApiResult.Success(runtime.copy(isAvailable = false, problemCode = "docker.unavailable")) }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        assertEquals("unknown", (browser.state.value.applications as ApiResult.Success).value.single().actualState)
        assertFalse((browser.state.value.runtime as ApiResult.Success).value.isAvailable)
    }

    @Test fun `failed refresh discards previously successful runtime and detail`() = runTest {
        signIn(); reads()
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        browser.select(app.id); runCurrent()
        gateway.onDeploymentRuntime = { _, _ -> ApiResult.Transport(null) }
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Transport(null) }
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Transport(null) }
        browser.refresh(); runCurrent()
        assertTrue(browser.state.value.runtime is ApiResult.Transport)
        assertTrue(browser.state.value.applications is ApiResult.Transport)
        assertTrue(browser.state.value.detail is ApiResult.Transport)
    }

    @Test fun `permission rejection remains distinct from transport failure without elevation`() = runTest {
        signIn(); reads()
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Problem(403, "", null) }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        assertEquals(403, (browser.state.value.applications as ApiResult.Problem).status)
        assertEquals(0, gateway.elevationCount)
    }

    @Test fun `signout clears selected application and all loaded data`() = runTest {
        signIn(); reads()
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        session.clearSession(); runCurrent()
        assertEquals(DeploymentBrowserState(), browser.state.value)
    }

    @Test fun `late old account response cannot populate the new account`() = runTest {
        signIn(); reads()
        val gate = CompletableDeferred<Unit>()
        gateway.onDeploymentApplications = { _, _ ->
            withContext(NonCancellable) { gate.await() }
            ApiResult.Success(listOf(app))
        }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Success(emptyList()) }
        signIn(user = "other"); runCurrent()
        gate.complete(Unit); runCurrent()
        assertEquals("other", browser.state.value.owner!!.userName)
        assertTrue((browser.state.value.applications as ApiResult.Success).value.isEmpty())
        assertNull(browser.state.value.selectedId)
    }

    @Test fun `late detail response cannot replace newly selected application`() = runTest {
        signIn(); reads()
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        val gate = CompletableDeferred<Unit>()
        gateway.onDeploymentSnapshot = { _, _, id ->
            if (id == "first") withContext(NonCancellable) { gate.await() }
            ApiResult.Success(DeploymentSnapshot(app.copy(id = id), emptyList(), emptyList(), null))
        }
        browser.select("first"); runCurrent()
        browser.select("second"); runCurrent()
        gate.complete(Unit); runCurrent()
        assertEquals("second", (browser.state.value.detail as ApiResult.Success).value.application.id)
        assertFalse(browser.state.value.detailLoading)
    }

    @Test fun `logs are opt in then expand from a small tail`() = runTest {
        signIn(); reads()
        val requestedTails = mutableListOf<Int>()
        gateway.onDeploymentLogs = { _, _, _, tail ->
            requestedTails += tail
            ApiResult.Success(DeploymentLog((1..tail).map { "line-$it" }, truncated = tail < 1_000))
        }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()

        assertTrue(requestedTails.isEmpty())
        assertNull(browser.state.value.logs)

        browser.loadLogs(); runCurrent()
        assertEquals(listOf(20), requestedTails)
        assertEquals(20, browser.state.value.loadedLogTail)
        assertEquals(20, (browser.state.value.logs as ApiResult.Success).value.lines.size)

        browser.loadMoreLogs(); runCurrent()
        assertEquals(listOf(20, 100), requestedTails)
        assertEquals(100, browser.state.value.loadedLogTail)
    }

    @Test fun `busy log read cannot be cancelled and resent by duplicate entry`() = runTest {
        signIn(); reads()
        val gate = CompletableDeferred<ApiResult<DeploymentLog>>()
        val tails = mutableListOf<Int>()
        gateway.onDeploymentLogs = { _, _, _, tail -> tails.add(tail); gate.await() }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.loadLogs(100); runCurrent()
        browser.loadLogs(20); runCurrent()
        assertEquals(listOf(100), tails)
        gate.complete(ApiResult.Success(DeploymentLog(listOf("review log"), false))); runCurrent()
        assertEquals(100, browser.state.value.loadedLogTail)
    }

    @Test fun `unexpected log read exception produces safe retryable result`() = runTest {
        signIn(); reads()
        gateway.onDeploymentLogs = { _, _, _, _ -> throw IllegalStateException("private log detail") }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.loadLogs(100); runCurrent()
        assertEquals(ApiResult.Transport(null), browser.state.value.logs)
        assertEquals(100, browser.state.value.loadedLogTail)
        assertFalse(browser.state.value.logsLoading)
    }

    @Test fun `retry keeps failed expanded tail and replaces rather than appends output`() = runTest {
        signIn(); reads()
        val tails = mutableListOf<Int>()
        gateway.onDeploymentLogs = { _, _, _, tail ->
            tails.add(tail)
            when (tails.size) {
                1 -> ApiResult.Success(DeploymentLog(listOf("old line"), true))
                2 -> ApiResult.Transport(null)
                else -> ApiResult.Success(DeploymentLog(listOf("new line"), false))
            }
        }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.loadLogs(); runCurrent(); browser.loadMoreLogs(); runCurrent()
        assertEquals(ApiResult.Transport(null), browser.state.value.logs)
        browser.retryLogs(); runCurrent()
        assertEquals(listOf(20, 100, 100), tails)
        assertEquals(listOf("new line"), (browser.state.value.logs as ApiResult.Success).value.lines)
    }

    @Test fun `late logs cannot populate another selected application`() = runTest {
        signIn(); reads()
        val gate = CompletableDeferred<Unit>()
        gateway.onDeploymentLogs = { _, _, _, _ ->
            withContext(NonCancellable) { gate.await() }
            ApiResult.Success(DeploymentLog(listOf("old app line"), false))
        }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.loadLogs(); runCurrent()
        browser.select("other"); runCurrent(); gate.complete(Unit); runCurrent()
        assertEquals("other", browser.state.value.selectedId)
        assertNull(browser.state.value.logs); assertNull(browser.state.value.loadedLogTail)
        assertFalse(browser.state.value.logsLoading)
    }

    @Test fun `late logs cannot populate a replacement login session`() = runTest {
        signIn(); reads()
        val gate = CompletableDeferred<Unit>()
        gateway.onDeploymentLogs = { _, _, _, _ ->
            withContext(NonCancellable) { gate.await() }
            ApiResult.Success(DeploymentLog(listOf("old account line"), false))
        }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.loadLogs(); runCurrent()
        signIn(user = "other"); runCurrent(); gate.complete(Unit); runCurrent()
        assertEquals("other", browser.state.value.owner?.userName)
        assertNull(browser.state.value.selectedId); assertNull(browser.state.value.logs)
        assertFalse(browser.state.value.logsLoading)
    }

    @Test fun `log cancellation propagates without becoming a transport failure`() = runTest {
        signIn(); reads()
        gateway.onDeploymentLogs = { _, _, _, _ -> throw CancellationException("cancelled read") }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.loadLogs(); runCurrent()
        assertNull(browser.state.value.logs)
        assertFalse(browser.state.value.logsLoading)
    }

    @Test fun `rollback ignores the current revision and queues a selected older revision once`() = runTest {
        signIn(); reads()
        val current = DeploymentRevision("revision-current", 2, "image@sha256:current", true, null, null)
        val older = DeploymentRevision("revision-older", 1, "image@sha256:older", false, null, null)
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(app, listOf(current, older), emptyList(), null)) }
        val calls = mutableListOf<List<String>>()
        gateway.onRollbackDeployment = { _, _, applicationId, revisionId, key ->
            calls += listOf(applicationId, revisionId, key)
            ApiResult.Success(DeploymentOperation("operation-1", applicationId, "rollback", "queued", "queued", null, null, null, null, true))
        }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()

        browser.rollback(current); runCurrent()
        assertTrue(calls.isEmpty())

        browser.rollback(older); runCurrent()
        assertEquals(app.id, calls.single()[0])
        assertEquals(older.id, calls.single()[1])
        assertTrue(calls.single()[2].isNotBlank())
        assertEquals("rollback", (browser.state.value.submission as ApiResult.Success).value.kind)
    }

    @Test fun `duplicate rollback cannot cancel and resend an in flight write`() = runTest {
        signIn(); reads()
        val older = DeploymentRevision("revision-older", 1, "image:old", false, null, null)
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(app, listOf(older), emptyList(), null)) }
        val receipt = CompletableDeferred<ApiResult<DeploymentOperation>>()
        var sends = 0
        gateway.onRollbackDeployment = { _, _, _, _, _ -> sends++; receipt.await() }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.rollback(older); runCurrent(); browser.rollback(older); runCurrent()
        receipt.complete(ApiResult.Transport(null)); runCurrent()
        assertEquals(1, sends)
    }

    @Test fun `refresh and selection cannot cancel an in flight rollback`() = runTest {
        signIn(); reads()
        val older = DeploymentRevision("revision-older", 1, "image:old", false, null, null)
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(app, listOf(older), emptyList(), null)) }
        val receipt = CompletableDeferred<ApiResult<DeploymentOperation>>()
        var cancelled = false
        gateway.onRollbackDeployment = { _, _, _, _, _ ->
            try { receipt.await() } catch (failure: CancellationException) { cancelled = true; throw failure }
        }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.rollback(older); runCurrent(); browser.refresh(); browser.select("other"); runCurrent()
        val busy = browser.state.value.submitting
        val selected = browser.state.value.selectedId
        receipt.complete(ApiResult.Transport(null)); runCurrent()
        assertFalse(cancelled); assertTrue(busy); assertEquals(app.id, selected)
    }

    @Test fun `rollback rejects a revision absent from the current application snapshot`() = runTest {
        signIn(); reads()
        var sends = 0
        gateway.onRollbackDeployment = { _, _, _, _, _ -> sends++; ApiResult.Transport(null) }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.rollback(DeploymentRevision("foreign-revision", 1, "image:foreign", false, null, null)); runCurrent()
        assertEquals(0, sends)
    }

    @Test fun `busy rollback blocks different mutation entries without cancelling original request`() = runTest {
        signIn(); reads()
        val older = DeploymentRevision("revision-older", 1, "image:old", false, null, null)
        val operation = DeploymentOperation("review-operation", app.id, "rollback", "queued", "queued", null, null, null, null, true)
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(app, listOf(older), emptyList(), null)) }
        val receipt = CompletableDeferred<ApiResult<DeploymentOperation>>()
        var otherSends = 0
        gateway.onRollbackDeployment = { _, _, _, _, _ -> receipt.await() }
        gateway.onDeploymentLifecycle = { _, _, _, _, _ -> otherSends++; ApiResult.Transport(null) }
        gateway.onDeleteDeployment = { _, _, _, _ -> otherSends++; ApiResult.Transport(null) }
        gateway.onCancelDeploymentOperation = { _, _, _, _ -> otherSends++; ApiResult.Transport(null) }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.rollback(older); runCurrent()
        browser.lifecycle(DeploymentLifecycleAction.Restart); browser.delete(); browser.cancel(operation); runCurrent()
        assertTrue(browser.state.value.submitting)
        receipt.complete(ApiResult.Transport(null)); runCurrent()
        assertEquals(0, otherSends)
    }

    @Test fun `rollback rejects an active operation and altered revision facts`() = runTest {
        signIn(); reads()
        val older = DeploymentRevision("revision-older", 1, "image:old", false, null, null)
        val operation = DeploymentOperation("review-operation", app.id, "deploy", "running", "deploy", null, null, null, null, true)
        var active: DeploymentOperation? = operation
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(app, listOf(older), emptyList(), active)) }
        var sends = 0
        gateway.onRollbackDeployment = { _, _, _, _, _ -> sends++; ApiResult.Transport(null) }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        browser.rollback(older); runCurrent()
        active = null; browser.select(app.id); runCurrent()
        browser.rollback(older.copy(imageReference = "image:changed")); runCurrent()
        assertEquals(0, sends)
    }

    @Test fun `restored controls remain reachable when application is absent and refresh cannot clear them`() = runTest {
        signIn(); reads()
        val owner = session.state.value as SessionState.Active
        val journal = DeploymentControlJournal(MemoryDeploymentControlStorage())
        val pending = PendingDeploymentControl(owner.serviceId, owner.userName, "removed-app", DeploymentControlKind.Delete, "", "original-key")
        journal.begin(pending)
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Success(emptyList()) }
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Problem(404, "missing", null) }
        val restored = DeploymentRepository(gateway, session, controlJournal = journal)
        val browser = DeploymentBrowser(restored, session, backgroundScope)
        runCurrent()
        assertEquals(listOf(pending), browser.state.value.pendingControls)
        browser.select(pending.applicationId); runCurrent(); browser.refresh(); runCurrent()
        assertEquals(pending, browser.state.value.pendingControl)
        assertTrue(browser.state.value.controlBlocked)
    }

    @Test fun `expired access token retries read with refreshed token once`() = runTest {
        signIn()
        val tokens = mutableListOf<String>()
        gateway.onDeploymentApplications = { _, token ->
            tokens += token
            if (tokens.size == 1) ApiResult.Problem(401, "", null) else ApiResult.Success(emptyList())
        }
        gateway.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("new-token", "new-refresh", null, null)) }
        assertTrue(repository.applications(session.state.value as SessionState.Active) is ApiResult.Success)
        assertEquals(listOf("access-1", "new-token"), tokens)
        assertEquals(1, gateway.refreshCount)
    }

    @Test fun `previous server owner cannot initiate a read on the new server`() = runTest {
        signIn()
        val owner = session.state.value as SessionState.Active
        signIn(url = "https://another.local")
        try {
            repository.applications(owner)
            fail("Expected cancellation before any read")
        } catch (_: CancellationException) { }
    }

    @Test fun `overlapping list and runtime reads rotate the expired token only once`() = runTest {
        signIn()
        val owner = session.state.value as SessionState.Active
        val gate = CompletableDeferred<Unit>()
        val tokens = mutableListOf<String>()
        gateway.onDeploymentApplications = { _, token ->
            tokens += token
            if (token == "access-1") {
                gate.await()
                ApiResult.Problem(401, "", null)
            } else ApiResult.Success(emptyList())
        }
        gateway.onDeploymentRuntime = { _, token ->
            tokens += token
            ApiResult.Success(runtime)
        }
        gateway.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("new-token", "new-refresh", null, null)) }
        val applications = async { repository.applications(owner) }
        val engine = async { repository.runtime(owner) }
        runCurrent()
        assertEquals(listOf("access-1"), tokens)
        gate.complete(Unit)
        assertTrue(applications.await() is ApiResult.Success)
        assertTrue(engine.await() is ApiResult.Success)
        assertEquals(listOf("access-1", "new-token", "new-token"), tokens)
        assertEquals(1, gateway.refreshCount)
    }
}
