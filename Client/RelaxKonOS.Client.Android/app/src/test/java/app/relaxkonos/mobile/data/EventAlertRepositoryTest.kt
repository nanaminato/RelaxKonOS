package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class EventAlertRepositoryTest {
    private val gateway = FakeGateway(); private val session = AuthSession(gateway)
    private val repository = EventAlertRepository(gateway, session)
    private fun alert(id: String) = OperationalAlert(id, "certificate.expiring_soon", "warning", "open", "", 1, null, "certificate", "certificate", null)
    private suspend fun signIn(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `detail for another alert cannot supply the selected remediation`() = runTest {
        val owner = signIn(); gateway.onAlertDetail = { ApiResult.Success(OperationalAlertDetail(alert("other"), emptyList())) }
        assertTrue(repository.detail(owner, "selected") is ApiResult.Transport)
        gateway.onAlertDetail = { ApiResult.Success(OperationalAlertDetail(alert(it), emptyList())) }
        assertEquals("selected", (repository.detail(owner, "selected") as ApiResult.Success).value.alert.id)
    }
    @Test fun `acknowledgement cannot replace a different alert in the selected list`() = runTest {
        val owner = signIn(); gateway.onAcknowledgeAlert = { ApiResult.Success(alert("other")) }
        assertTrue(repository.acknowledge(owner, "selected") is ApiResult.Transport)
        gateway.onAcknowledgeAlert = { ApiResult.Success(alert(it).copy(status = "acknowledged")) }
        assertEquals("acknowledged", (repository.acknowledge(owner, "selected") as ApiResult.Success).value.status)
    }
}
