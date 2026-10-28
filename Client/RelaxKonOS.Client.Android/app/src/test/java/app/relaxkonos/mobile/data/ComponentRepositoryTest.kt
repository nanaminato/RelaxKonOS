package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ComponentModeSupport
import app.relaxkonos.mobile.core.net.ComponentState
import app.relaxkonos.mobile.core.net.ComponentStateView
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ComponentRepositoryTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val repository = ComponentRepository(gateway, session)

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
}
