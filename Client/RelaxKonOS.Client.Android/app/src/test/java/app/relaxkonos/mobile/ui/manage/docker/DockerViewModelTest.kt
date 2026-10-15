package app.relaxkonos.mobile.ui.manage.docker

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.*
import org.junit.Assert.*
import java.io.ByteArrayInputStream
import java.util.concurrent.CountDownLatch

@OptIn(ExperimentalCoroutinesApi::class)
class DockerViewModelTest {
    private val base = FakeGateway()
    private var cancelRead = false
    private var failRead = false
    private var previewRead: suspend () -> ApiResult<DockerStackPreview> = { ApiResult.Transport(null) }
    private var deployments = 0
    private var previewAttempts = 0
    private var serviceRead: suspend (String) -> ApiResult<List<DockerStackService>> = { ApiResult.Success(emptyList()) }
    private var historyRead: suspend (String) -> ApiResult<List<DockerStackOperation>> = { ApiResult.Success(emptyList()) }
    private var operationRead: suspend () -> ApiResult<DockerStackOperation> = { ApiResult.Transport(null) }
    private var diagnosticsRead: suspend () -> ApiResult<DockerStackOperationDiagnostics> = { ApiResult.Success(DockerStackOperationDiagnostics(emptyList(), false)) }
    private var logsRead: suspend (String) -> ApiResult<DockerLogs> = { ApiResult.Success(DockerLogs(listOf(it), false)) }
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun dockerStackDeploy(serverUrl: String, accessToken: String, name: String, composeYaml: String, definitionVersion: String, idempotencyKey: String): ApiResult<DockerStackOperation> {
            deployments++; error("Unexpected deployment")
        }
        override suspend fun dockerStackPreview(serverUrl: String, accessToken: String, name: String, composeYaml: String): ApiResult<DockerStackPreview> {
            previewAttempts++; return previewRead()
        }
        override suspend fun dockerStatus(serverUrl: String, accessToken: String): ApiResult<DockerStatus> {
            if (cancelRead) throw CancellationException("cancelled read")
            if (failRead) error("private read error")
            return ApiResult.Transport(null)
        }
        override suspend fun dockerStacks(serverUrl: String, accessToken: String) = ApiResult.Success(emptyList<DockerStack>())
        override suspend fun dockerStackServices(serverUrl: String, accessToken: String, name: String) = serviceRead(name)
        override suspend fun dockerStackOperations(serverUrl: String, accessToken: String, name: String, limit: Int) = historyRead(name)
        override suspend fun dockerStackOperation(serverUrl: String, accessToken: String, operationId: String) = operationRead()
        override suspend fun dockerStackOperationDiagnostics(serverUrl: String, accessToken: String, operationId: String) = diagnosticsRead()
        override suspend fun dockerContainerLogs(serverUrl: String, accessToken: String, id: String, tail: Int) = logsRead(id)
    }
    private val session = AuthSession(gateway)
    private val repository = DockerRepository(gateway, session, OperationIndex(object : OperationIndexStorage {
        override fun read(): ByteArray? = null
        override fun write(bytes: ByteArray) {}
    }), testDockerGate())
    private val store = ViewModelStore()
    private lateinit var model: DockerViewModel
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = DockerViewModel(session, repository); store.put("docker", model)
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login() {
        base.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.DOCKER), privilegedOperations = true)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://docker-review.invalid"), "review", "test".toCharArray()) {}
    }
    @Test fun `cancelled preview releases busy and permits another preview`() = runTest {
        login(); runCurrent()
        previewRead = { throw CancellationException("private cancelled preview") }
        model.preview("review", "services: {}"); runCurrent()
        assertFalse(model.state.busy); assertNull(model.state.preview)
        previewRead = { ApiResult.Problem(400, "docker.stack.invalid_yaml", null) }
        model.preview("review", "services: {}"); runCurrent()
        assertFalse(model.state.busy); assertNotNull(model.state.message)
    }
    @Test fun `cancelled deployment preflight releases editor without submitting`() = runTest {
        login(); runCurrent(); previewRead = { throw CancellationException("cancelled preflight") }
        var closed = 0
        model.deploy("review", "services: {}") { closed++ }; runCurrent()
        assertFalse(model.state.busy); assertNull(model.state.preview)
        assertEquals(0, deployments); assertEquals(0, closed)
        assertEquals(1, previewAttempts)
    }
    @Test fun `deployment preflight exception releases editor without submitting`() = runTest {
        login(); runCurrent(); previewRead = { error("private preflight exception") }
        model.deploy("review", "services: {}") { error("Unexpected close") }; runCurrent()
        assertFalse(model.state.busy); assertNull(model.state.preview)
        assertEquals(0, deployments); assertNotNull(model.state.message); assertNull(model.state.message?.debugDetail)
        assertEquals(1, previewAttempts)
    }
    @Test fun `unexpected preview exception clears busy and sanitizes feedback`() = runTest {
        login(); runCurrent(); previewRead = { error("private parser exception") }
        model.preview("review", "services: {}"); runCurrent()
        assertFalse(model.state.busy); assertNull(model.state.preview)
        assertNotNull(model.state.message); assertNull(model.state.message?.debugDetail)
    }
    @Test fun `cancelled refresh releases loading and permits explicit recovery`() = runTest {
        login(); runCurrent(); cancelRead = true; model.refresh(); runCurrent()
        assertFalse(model.state.loading)
        cancelRead = false; model.refresh(); runCurrent(); assertNotNull(model.state.stacks)
    }
    @Test fun `unexpected read failure releases loading with sanitized feedback`() = runTest {
        login(); runCurrent(); failRead = true; model.refresh(); runCurrent()
        assertFalse(model.state.loading); assertNotNull(model.state.message); assertNull(model.state.message?.debugDetail)
    }
    @Test fun `late first selection cannot replace a fresh same stack selection`() = runTest {
        login(); runCurrent()
        val reply = CompletableDeferred<ApiResult<List<DockerStackService>>>()
        var reads = 0
        val fresh = DockerStackService("fresh", "fresh", "image", "running", "running")
        serviceRead = { reads++; if (reads == 1) withContext(NonCancellable) { reply.await() } else ApiResult.Success(listOf(fresh)) }
        val a = DockerStack("a", "running", "", "")
        model.selectStack(a); runCurrent()
        model.selectStack(a.copy(name = "b")); model.selectStack(a); runCurrent()
        reply.complete(ApiResult.Success(listOf(fresh.copy(service = "old")))); runCurrent()
        assertEquals(listOf(fresh), (model.state.services as ApiResult.Success).value)
    }
    @Test fun `selection failure exposes retryable sanitized results`() = runTest {
        login(); runCurrent()
        serviceRead = { error("private selection failure") }
        val stack = DockerStack("a", "running", "", "")
        model.selectStack(stack); runCurrent()
        assertTrue(model.state.services is ApiResult.Transport)
        assertTrue(model.state.operations is ApiResult.Transport)
        assertNull(model.state.message?.debugDetail)
        serviceRead = { ApiResult.Success(emptyList()) }
        model.selectStack(stack); runCurrent()
        assertTrue(model.state.services is ApiResult.Success)
        assertTrue(model.state.operations is ApiResult.Success)
    }
    @Test fun `late cancelled selection cannot publish failure over new results`() = runTest {
        login(); runCurrent()
        model.dismissMessage()
        val reply = CompletableDeferred<Unit>()
        var reads = 0
        serviceRead = {
            reads++
            if (reads == 1) {
                withContext(NonCancellable) { reply.await() }
                throw CancellationException("private old read")
            }
            ApiResult.Success(emptyList())
        }
        val stack = DockerStack("a", "running", "", "")
        model.selectStack(stack); runCurrent()
        model.selectStack(stack); runCurrent()
        reply.complete(Unit); runCurrent()
        assertTrue(model.state.services is ApiResult.Success)
        assertTrue(model.state.operations is ApiResult.Success)
        assertNull(model.state.message)
    }
    private fun operation() = DockerStackOperation("11111111-1111-1111-1111-111111111111", "a", DockerStackOperationKind.DEPLOY,
        DockerStackOperationState.RUNNING, "running", null, null, "2026-10-15T00:00:00Z", true, emptyList())

    @Test fun `new import supersedes blocked old document without overwriting draft`() = runTest {
        login(); runCurrent()
        val owner = session.state.value as SessionState.Active
        val started = CompletableDeferred<Unit>()
        val release = CountDownLatch(1)
        val imported = mutableListOf<String>()
        val old = model.importCompose(owner, { started.complete(Unit); release.await(); ByteArrayInputStream("old".toByteArray()) }, imported::add)!!
        try {
            runCurrent(); started.await()
            model.importCompose(owner, { ByteArrayInputStream("fresh".toByteArray()) }, imported::add)!!.join()
        } finally { release.countDown(); old.join() }
        assertEquals(listOf("fresh"), imported)
    }
    @Test fun `abandoned import failure cannot show feedback on new editor`() = runTest {
        login(); runCurrent(); model.dismissMessage()
        val started = CompletableDeferred<Unit>()
        val release = CountDownLatch(1)
        val job = model.importCompose(session.state.value as SessionState.Active, { started.complete(Unit); release.await(); error("private old import") }) { fail("old import published") }!!
        try { runCurrent(); started.await(); model.cancelImport() }
        finally { release.countDown(); job.join() }
        assertNull(model.state.message)
    }
    @Test fun `picker result from former login is rejected before opening document`() = runTest {
        login(); runCurrent()
        val oldOwner = session.state.value as SessionState.Active
        login(); runCurrent()
        var opened = false
        assertNull(model.importCompose(oldOwner, { opened = true; ByteArrayInputStream(byteArrayOf()) }) { fail() })
        assertFalse(opened)
    }

    @Test fun `closing pending logs does not read again or reopen on late reply`() = runTest {
        login(); runCurrent(); model.dismissMessage()
        val reply = CompletableDeferred<ApiResult<DockerLogs>>()
        var reads = 0
        logsRead = { reads++; withContext(NonCancellable) { reply.await() } }
        model.loadLogs("a"); runCurrent()
        model.loadLogs("a"); runCurrent()
        reply.complete(ApiResult.Success(DockerLogs(listOf("old"), false))); runCurrent()
        assertEquals(1, reads); assertNull(model.state.logs); assertNull(model.state.message)
    }
    @Test fun `project selection withdraws inflight logs`() = runTest {
        login(); runCurrent(); model.dismissMessage()
        val reply = CompletableDeferred<ApiResult<DockerLogs>>()
        logsRead = { withContext(NonCancellable) { reply.await() } }
        model.loadLogs("a"); runCurrent()
        model.selectStack(DockerStack("b", "running", "", "")); runCurrent()
        reply.complete(ApiResult.Success(DockerLogs(listOf("old project"), false))); runCurrent()
        assertNull(model.state.logs); assertNull(model.state.message)
    }
    @Test fun `logs exception is sanitized and explicit retry succeeds then closes without read`() = runTest {
        login(); runCurrent(); model.dismissMessage()
        logsRead = { error("private logs failure") }
        model.loadLogs("a"); runCurrent()
        assertNotNull(model.state.message); assertNull(model.state.message?.debugDetail)
        var reads = 0
        logsRead = { reads++; ApiResult.Success(DockerLogs(listOf("fresh"), false)) }
        model.loadLogs("a"); runCurrent()
        assertEquals(listOf("fresh"), model.state.logs?.second?.lines)
        model.loadLogs("a"); runCurrent()
        assertEquals(1, reads); assertNull(model.state.logs)
    }

    @Test fun `automatic refresh preserves terminal outcome even when resource reads fail`() = runTest {
        login(); runCurrent()
        for ((terminal, message) in listOf(
            DockerStackOperationState.SUCCEEDED to app.relaxkonos.mobile.R.string.docker_stack_operation_succeeded,
            DockerStackOperationState.PARTIAL_FAILED to app.relaxkonos.mobile.R.string.docker_stack_operation_partial,
            DockerStackOperationState.FAILED to app.relaxkonos.mobile.R.string.docker_stack_operation_failed)) {
            model.dismissMessage()
            var current = operation()
            historyRead = { ApiResult.Success(listOf(current)) }
            operationRead = { current = current.copy(state = terminal); ApiResult.Success(current) }
            model.selectStack(DockerStack("a", "running", "", "")); runCurrent()
            advanceTimeBy(1000); runCurrent()
            assertFalse(model.state.loading)
            assertTrue(model.state.status is ApiResult.Transport)
            assertEquals(message, model.state.message?.resId)
            model.refresh(); runCurrent()
            assertEquals(app.relaxkonos.mobile.R.string.error_connectivity, model.state.message?.resId)
        }
    }

    @Test fun `paused tracking stops reads and resumes original operation without mutation`() = runTest {
        login(); runCurrent(); model.dismissMessage()
        var polls = 0
        historyRead = { ApiResult.Success(listOf(operation())) }
        operationRead = { polls++; ApiResult.Success(operation()) }
        model.selectStack(DockerStack("a", "running", "", "")); runCurrent()
        advanceTimeBy(1000); runCurrent(); assertEquals(1, polls)
        model.setTrackingActive(false)
        advanceTimeBy(5000); runCurrent(); assertEquals(1, polls)
        assertNull(model.state.message)
        model.setTrackingActive(true)
        advanceTimeBy(1000); runCurrent(); assertEquals(2, polls)
        assertEquals(0, deployments)
        model.setTrackingActive(false)
    }

    @Test fun `late diagnostics cannot leak into a different project`() = runTest {
        login(); runCurrent(); model.dismissMessage()
        val reply = CompletableDeferred<ApiResult<DockerStackOperationDiagnostics>>()
        historyRead = { if (it == "a") ApiResult.Success(listOf(operation())) else ApiResult.Success(emptyList()) }
        operationRead = { ApiResult.Success(operation().copy(state = DockerStackOperationState.SUCCEEDED)) }
        diagnosticsRead = { withContext(NonCancellable) { reply.await() } }
        model.selectStack(DockerStack("a", "running", "", "")); runCurrent()
        advanceTimeBy(1000); runCurrent()
        model.selectStack(DockerStack("b", "running", "", "")); runCurrent()
        reply.complete(ApiResult.Success(DockerStackOperationDiagnostics(listOf("old project output"), false))); runCurrent()
        assertEquals("b", model.state.selectedStack?.name)
        assertNull(model.state.diagnostics); assertNull(model.state.message)
        assertEquals(emptyList<DockerStackOperation>(), (model.state.operations as ApiResult.Success).value)
    }
    @Test fun `operation polling exception is sanitized and allows reselection`() = runTest {
        login(); runCurrent(); model.dismissMessage()
        historyRead = { ApiResult.Success(listOf(operation())) }
        operationRead = { error("private operation failure") }
        val stack = DockerStack("a", "running", "", "")
        model.selectStack(stack); runCurrent(); advanceTimeBy(1000); runCurrent()
        assertNotNull(model.state.message); assertNull(model.state.message?.debugDetail)
        historyRead = { ApiResult.Success(emptyList()) }
        model.selectStack(stack); runCurrent()
        assertTrue(model.state.operations is ApiResult.Success)
    }
}
