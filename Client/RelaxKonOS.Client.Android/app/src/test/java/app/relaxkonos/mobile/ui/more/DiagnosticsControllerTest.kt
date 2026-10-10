package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.SystemRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class DiagnosticsControllerTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private var files: suspend () -> ApiResult<Int> = { ApiResult.Success(3) }
    private suspend fun login() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.METRICS, ServerCapabilities.FILES)))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://diagnostics-review.invalid"), "review", "test".toCharArray()) {}
        gateway.onPerformance = { _, _ -> ApiResult.Success(PerformanceWire.snapshot(PerformanceWireTest.SNAPSHOT)) }
    }
    private fun model(scope: TestScope) = DiagnosticsController(session, SystemRepository(gateway, session), { owner ->
        assertSame(session.state.value, owner); files()
    },
        { DiagnosticsDeviceFacts("test", 1, 2, false, "Unavailable", false) },
        { id, args -> "$id:${args.joinToString()}" }, scope.backgroundScope)
    @Test fun `new login clears old diagnostic lines and an open export`() = runTest {
        login(); val model = model(this); model.run(); runCurrent(); model.buildExport()
        assertTrue(model.lines.isNotEmpty()); assertNotNull(model.export)
        login(); runCurrent()
        assertTrue(model.lines.isEmpty()); assertNull(model.export); assertFalse(model.running)
    }
    @Test fun `cancelled diagnostics end busy and allow a new check`() = runTest {
        login(); val model = model(this)
        gateway.onPerformance = { _, _ -> throw CancellationException("cancelled") }
        model.run(); runCurrent(); assertFalse(model.running)
        login(); model.run(); runCurrent(); assertTrue(model.lines.isNotEmpty())
    }
    @Test fun `file provider exception is a safe read outcome rather than an unhandled failure`() = runTest {
        login(); val model = model(this); files = { error("private file provider detail") }
        model.run(); runCurrent()
        assertFalse(model.running)
        assertTrue(model.lines.any { it.startsWith("${R.string.diagnostics_files_transport}:") })
        model.buildExport(); assertFalse(model.export!!.contains("private file provider detail"))
    }
    @Test fun `owner change during a file read cannot publish mixed account results`() = runTest {
        login(); val model = model(this)
        files = { login(); ApiResult.Success(42) }
        model.run(); runCurrent()
        assertTrue(model.lines.isEmpty()); assertFalse(model.running); assertNull(model.export)
    }
}
