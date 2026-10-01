package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class EventAlertRepositoryTest {
    private val gateway = FakeGateway(); private val session = AuthSession(gateway)
    private val repository = EventAlertRepository(gateway, session)
    private fun alert(id: String = "selected") = OperationalAlert(id, "deployment.operation_failed", "error", "open", "deployment.failed", 1, 1,
        "applicationDeployment", "app", null, 0, "event", null, null, null)
    private suspend fun signIn(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private fun detail(alert: OperationalAlert) = OperationalAlertDetail(alert, emptyList(), emptyList())
    @Test fun `detail for another alert cannot supply selected remediation`() = runTest {
        val owner = signIn(); gateway.onAlertDetail = { ApiResult.Success(detail(alert("other"))) }
        assertTrue(repository.detail(owner, "selected") is ApiResult.Transport)
    }
    @Test fun `confirmed acknowledgement verifies exact readback`() = runTest {
        val owner = signIn(); var current = alert(); var writes = 0
        gateway.onAlertDetail = { ApiResult.Success(detail(current)) }
        gateway.onMutateAlert = { id, action, note, expiry ->
            assertEquals("selected", id); assertEquals(AlertMutation.Acknowledge, action); assertEquals("checked", note); assertNull(expiry)
            writes++; current = current.copy(status = "acknowledged"); ApiResult.Success(current)
        }
        val outcome = repository.mutate(owner, alert(), AlertMutation.Acknowledge, "checked")
        assertTrue(outcome.result is ApiResult.Success); assertFalse(outcome.mayHaveApplied); assertEquals(1, writes)
    }
    @Test fun `changed baseline and source owned resolution do not dispatch`() = runTest {
        val owner = signIn(); gateway.onAlertDetail = { ApiResult.Success(detail(alert().copy(occurrenceCount = 2))) }
        val stale = repository.mutate(owner, alert(), AlertMutation.Acknowledge)
        assertEquals(409, (stale.result as ApiResult.Problem).status)
        val automatic = repository.mutate(owner, alert().copy(type = "certificate.expiring_soon"), AlertMutation.Resolve, "done")
        assertEquals(400, (automatic.result as ApiResult.Problem).status)
    }
    @Test fun `lost receipt blocks replay and unchanged reconciliation does not clear gate`() = runTest {
        val owner = signIn(); var current = alert(); var writes = 0
        gateway.onAlertDetail = { ApiResult.Success(detail(current)) }
        gateway.onMutateAlert = { _, _, _, _ -> writes++; ApiResult.Transport(null) }
        assertTrue(repository.mutate(owner, alert(), AlertMutation.Acknowledge).mayHaveApplied)
        repository.reconcile(owner, "selected"); assertTrue(repository.hasUnknown(owner, "selected"))
        repository.mutate(owner, alert(), AlertMutation.Acknowledge); assertEquals(1, writes)
        current = current.copy(status = "acknowledged")
        repository.reconcile(owner, "selected"); assertFalse(repository.hasUnknown(owner, "selected"))
    }
    @Test fun `wrong receipt cannot replace another alert and locks writes`() = runTest {
        val owner = signIn(); gateway.onAlertDetail = { ApiResult.Success(detail(alert())) }
        gateway.onMutateAlert = { _, _, _, _ -> ApiResult.Success(alert("other").copy(status = "acknowledged")) }
        val result = repository.mutate(owner, alert(), AlertMutation.Acknowledge)
        assertTrue(result.result is ApiResult.Transport); assertTrue(result.mayHaveApplied)
    }
    @Test fun `cancellation after dispatch retains uncertainty`() = runTest {
        val owner = signIn(); gateway.onAlertDetail = { ApiResult.Success(detail(alert())) }
        gateway.onMutateAlert = { _, _, _, _ -> throw CancellationException("Left page") }
        try { repository.mutate(owner, alert(), AlertMutation.Acknowledge); fail("Expected cancellation") } catch (_: CancellationException) { }
        assertTrue(repository.hasUnknown(owner, "selected"))
    }
    @Test fun `suppression requires a future expiry and nonblank bounded reason`() = runTest {
        val owner = signIn()
        listOf("", "x".repeat(513)).forEach { reason ->
            assertEquals(400, (repository.mutate(owner, alert(), AlertMutation.Suppress, reason, System.currentTimeMillis() + 600_000).result as ApiResult.Problem).status)
        }
        assertEquals(400, (repository.mutate(owner, alert(), AlertMutation.Suppress, "maintenance", System.currentTimeMillis()).result as ApiResult.Problem).status)
    }
}
