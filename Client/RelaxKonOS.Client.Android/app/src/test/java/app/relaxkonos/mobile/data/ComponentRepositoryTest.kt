package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ComponentChange
import app.relaxkonos.mobile.core.net.ComponentFrozenInstallationRequest
import app.relaxkonos.mobile.core.net.ComponentModeSupport
import app.relaxkonos.mobile.core.net.ComponentPlanResult
import app.relaxkonos.mobile.core.net.ComponentState
import app.relaxkonos.mobile.core.net.ComponentStateView
import app.relaxkonos.mobile.core.net.InstallationKind
import app.relaxkonos.mobile.core.net.InstallationOperation
import app.relaxkonos.mobile.core.net.InstallationRequest
import app.relaxkonos.mobile.core.net.InstallationService
import app.relaxkonos.mobile.core.net.InstallationStage
import app.relaxkonos.mobile.core.net.InstallationState
import app.relaxkonos.mobile.core.net.InstallationWire
import app.relaxkonos.mobile.core.net.WebServer
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ComponentRepositoryTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private var submitted: Pair<InstallationKind, InstallationRequest>? = null
    private val repository = ComponentRepository(gateway, session) { _, kind, request, _ ->
        submitted = kind to request
        ApiResult.Success(operation(kind))
    }

    private suspend fun signIn(privilegedOperations: Boolean = true): SessionState.Active {
        val login = loginSession().let {
            it.copy(server = it.server.copy(privilegedOperations = privilegedOperations))
        }
        gateway.onLogin = { _, _, _ -> ApiResult.Success(login) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }

    private fun view(componentId: String, version: String? = "1.24.0", ownership: String = "managed") =
        ComponentStateView(
            componentId = componentId,
            state = ComponentState(
                componentId = componentId,
                hostId = "host-1",
                revision = 7,
                ownership = ownership,
                mode = "systemPackage",
                target = "linux / x64 / systemd",
                activeVersion = version,
                recoveryRequired = false,
            ),
            modes = listOf(ComponentModeSupport("systemPackage", "linux-x64", available = true, problemCode = null)),
            problems = emptyList(),
            pendingOperationId = null,
        )

    private fun operation(kind: InstallationKind) = InstallationOperation(
        operationId = "11111111-1111-1111-1111-111111111111",
        service = InstallationService.Nginx,
        kind = kind,
        state = InstallationState.Running,
        stage = InstallationStage.Preparing,
        progress = null,
        problemCode = null,
        createdAtMillis = 0,
        startedAtMillis = null,
        completedAtMillis = null,
        cancellable = true,
    )

    private fun plan(componentId: String, change: ComponentChange) = ComponentPlanResult(
        componentId = componentId,
        change = change,
        problems = emptyList(),
        notes = emptyList(),
        requestJson = """{"componentId":"$componentId","change":"${change.wire}"}""",
        targetJson = """{"mode":"systemPackage"}""",
    )

    @Test
    fun `reads one state per component in request order`() = runTest {
        val owner = signIn()
        val requested = mutableListOf<String>()
        gateway.onComponentState = { id ->
            requested += id
            ApiResult.Success(view(id))
        }
        val result = repository.read(owner, listOf("nginx", "docker"))
        assertTrue(result is ApiResult.Success)
        val rows = (result as ApiResult.Success).value
        assertEquals(listOf("nginx", "docker"), requested)
        assertEquals(listOf("nginx", "docker"), rows.map { it.componentId })
        assertEquals("1.24.0", rows[0].view?.version)
        assertEquals("managed", rows[0].view?.ownership)
        assertNull(rows[0].problem)
    }

    @Test
    fun `one unreadable component keeps the other rows`() = runTest {
        val owner = signIn()
        gateway.onComponentState = { id ->
            if (id == "docker") ApiResult.Problem(404, "component.unknown", null) else ApiResult.Success(view(id))
        }
        val result = repository.read(owner, listOf("nginx", "docker"))
        val rows = (result as ApiResult.Success).value
        assertEquals("component.unknown", rows[1].problem)
        assertNull(rows[1].view)
        assertEquals("nginx", rows[0].componentId)
        assertTrue(rows[0].view != null)
    }

    @Test
    fun `a component that reports no state keeps its declared modes`() = runTest {
        val owner = signIn()
        gateway.onComponentState = {
            ApiResult.Success(ComponentStateView(
                componentId = it,
                state = null,
                modes = listOf(ComponentModeSupport("systemPackage", "linux-x64", available = false,
                    problemCode = "component.platform_unsupported")),
                problems = listOf("component.platform_unsupported"),
                pendingOperationId = null,
            ))
        }
        val rows = (repository.read(owner, listOf("samba")) as ApiResult.Success).value
        assertNull(rows[0].view?.state)
        assertTrue(rows[0].view?.unsupported == true)
        assertEquals(listOf("component.platform_unsupported"), rows[0].view?.problems)
    }

    @Test
    fun `all transport failures surface once`() = runTest {
        val owner = signIn()
        gateway.onComponentState = { ApiResult.Transport(null) }
        val result = repository.read(owner, listOf("nginx", "docker"))
        assertTrue(result is ApiResult.Transport)
    }

    @Test
    fun `missing privileged operations is refused before any request`() = runTest {
        val owner = signIn(privilegedOperations = false)
        var calls = 0
        gateway.onComponentState = { calls++; ApiResult.Success(view(it)) }
        var refused = false
        try {
            repository.read(owner, listOf("nginx"))
        } catch (_: IllegalArgumentException) {
            refused = true
        }
        assertTrue(refused)
        assertEquals(0, calls)
    }

    @Test
    fun `a session that changed while reading is never accepted`() = runTest {
        val first = signIn()
        val second = signIn()
        assertTrue(first !== second)
        var cancelled = false
        try {
            repository.read(first, listOf("nginx"))
        } catch (_: CancellationException) {
            cancelled = true
        }
        assertTrue(cancelled)
    }

    @Test
    fun `plan freezes the state revision and mode the user was shown`() = runTest {
        val owner = signIn()
        gateway.onComponentPlan = { componentId, change, body ->
            val json = JSONObject(String(body.toByteArray()))
            assertEquals("nginx", componentId)
            assertEquals(ComponentChange.Update, change)
            assertEquals("host-1", json.getString("hostId"))
            assertEquals(7L, json.getLong("expectedRevision"))
            assertEquals("SystemPackage", json.getString("mode"))
            assertTrue(json.getBoolean("keepData"))
            ApiResult.Success(plan(componentId, change))
        }
        val result = repository.plan(owner, "nginx", ComponentChange.Update, view("nginx").state!!, keepData = true)
        assertTrue(result is ApiResult.Success)
        assertTrue((result as ApiResult.Success).value.allowed)
    }

    @Test
    fun `apply submits the frozen package through the installation ledger`() = runTest {
        val owner = signIn()
        val frozen = plan("nginx", ComponentChange.Update)
        val result = repository.apply(owner, frozen, ElevationAnswerProvider.Declines)
        assertTrue(result is ApiResult.Success)
        val (kind, request) = submitted!!
        assertEquals(InstallationKind.Upgrade, kind)
        assertTrue(request is ComponentFrozenInstallationRequest)
        val body = JSONObject(String(InstallationWire.request(request, kind).toByteArray()))
        assertTrue(body.getBoolean("confirmed"))
        val packageValue = body.getJSONObject("frozenPackage")
        assertEquals("nginx", packageValue.getString("componentId"))
        assertTrue(packageValue.has("request") && packageValue.has("expected"))
    }

    @Test
    fun `frp changes are submitted as a frozen archive`() = runTest {
        val owner = signIn()
        val result = repository.apply(owner, plan("frpc", ComponentChange.Install), ElevationAnswerProvider.Declines)
        assertTrue(result is ApiResult.Success)
        val (kind, request) = submitted!!
        assertEquals(InstallationKind.Install, kind)
        val body = JSONObject(String(InstallationWire.request(request, kind).toByteArray()))
        assertTrue(body.has("frozenArchive"))
        assertTrue(!body.has("frozenPackage"))
    }

    @Test
    fun `a component without an installation owner is refused`() = runTest {
        val owner = signIn()
        val result = repository.apply(owner, plan("git", ComponentChange.Install), ElevationAnswerProvider.Declines)
        assertTrue(result is ApiResult.Transport)
        assertNull(submitted)
    }

    @Test
    fun `nginx selection reads the host current instance and sets another one`() = runTest {
        val owner = signIn()
        gateway.onCurrentNginx = { ApiResult.Success("nginx-a") }
        gateway.onWebServers = { ApiResult.Success(listOf(server("nginx-a"), server("nginx-b"))) }
        assertEquals("nginx-a", (repository.currentNginx(owner) as ApiResult.Success).value)
        val servers = (repository.nginxServers(owner) as ApiResult.Success).value
        assertEquals(listOf("nginx-a", "nginx-b"), servers.map { it.id })

        var selected: String? = null
        gateway.onSelectCurrentNginx = { id -> selected = id; ApiResult.Success(id) }
        assertEquals("nginx-b", (repository.selectCurrentNginx(owner, "nginx-b") as ApiResult.Success).value)
        assertEquals("nginx-b", selected)
    }

    private fun server(id: String) = WebServer(
        id = id,
        type = "nginx",
        managementMode = "integrated",
        version = "1.26.3",
        canRead = true,
        canTestConfiguration = true,
        providerId = "nginx",
        executablePath = "/usr/sbin/nginx",
        configurationPath = "/etc/nginx/nginx.conf",
        detectedAtMillis = 0,
        canReload = true,
        canStart = true,
        canStop = true,
        canRestart = true,
        canUninstall = false,
    )
}
