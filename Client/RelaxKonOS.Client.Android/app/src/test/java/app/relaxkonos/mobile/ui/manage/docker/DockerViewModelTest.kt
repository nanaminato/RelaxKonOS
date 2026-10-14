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

@OptIn(ExperimentalCoroutinesApi::class)
class DockerViewModelTest {
    private val base = FakeGateway()
    private var cancelRead = false
    private var failRead = false
    private var previewRead: suspend () -> ApiResult<DockerStackPreview> = { ApiResult.Transport(null) }
    private var deployments = 0
    private var previewAttempts = 0
    private var serviceRead: suspend (String) -> ApiResult<List<DockerStackService>> = { ApiResult.Success(emptyList()) }
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
        override suspend fun dockerStackOperations(serverUrl: String, accessToken: String, name: String, limit: Int) = ApiResult.Success(emptyList<DockerStackOperation>())
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
        model = DockerViewModel(session, repository) { null }; store.put("docker", model)
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
}
