package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import app.relaxkonos.mobile.ui.manage.deployments.DeploymentDefinitionDraft
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class DeploymentDefinitionRepositoryTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val repository = DeploymentRepository(gateway, session)
    private val baseline = deploymentFixture()
    private var actual = baseline
    private var sends = 0
    private var active: DeploymentOperation? = null
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://host.test"), "alice", "p".toCharArray()) {}
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(actual, emptyList(), emptyList(), active)) }
        gateway.onUpdateDeploymentDefinition = { id, update, key ->
            assertEquals(baseline.id, id); assertTrue(key.isNotEmpty()); sends++
            actual = actual.copy(name = update.name, updatedAt = "2026-10-01T00:00:01.1234567+00:00")
            ApiResult.Success(actual)
        }
        return session.state.value as SessionState.Active
    }
    private fun request() = DeploymentDefinitionDraft(baseline).also { it.name = "renamed" }.requestOrNull()!!

    @Test fun `saved definition requires a matching receipt and full readback without queuing runtime work`() = runTest {
        val owner = login()
        val outcome = repository.saveDefinition(owner, baseline, request(), "save-1")
        assertEquals("renamed", (outcome.result as ApiResult.Success).value.name)
        assertFalse(outcome.mayHaveSaved); assertEquals(1, sends); assertEquals(0, gateway.elevationCount)
        assertEquals(baseline.configuration, actual.configuration)
        assertEquals(baseline.volumes, actual.volumes)
    }
    @Test fun `stale timestamp and active operation refuse before sending`() = runTest {
        val owner = login()
        actual = actual.copy(updatedAt = "2026-10-01T00:00:00.1234568+00:00")
        assertEquals("application-deployment.definition_conflict", (repository.saveDefinition(owner, baseline, request(), "save").result as ApiResult.Problem).code)
        actual = baseline
        active = DeploymentOperation("op", baseline.id, "deploy", "queued", "preflight", null, null, null, 0, true)
        assertEquals("application-deployment.resource_conflict", (repository.saveDefinition(owner, baseline, request(), "save").result as ApiResult.Problem).code)
        assertEquals(0, sends)
    }
    @Test fun `lost save response remains unknown and is never replayed even if a later read looks identical`() = runTest {
        val owner = login()
        gateway.onUpdateDeploymentDefinition = { _, update, _ -> sends++; actual = actual.copy(name = update.name); ApiResult.Transport(null) }
        val outcome = repository.saveDefinition(owner, baseline, request(), "save")
        assertTrue(outcome.mayHaveSaved); assertTrue(outcome.result is ApiResult.Transport); assertEquals(1, sends)
        repository.snapshot(owner, baseline.id)
        assertEquals(1, sends)
    }
    @Test fun `receipt and readback mismatch do not claim success or replay`() = runTest {
        val owner = login()
        gateway.onUpdateDeploymentDefinition = { _, update, _ -> sends++; ApiResult.Success(baseline.copy(name = update.name, updatedAt = "2026-10-01T00:00:01+00:00")) }
        assertTrue(repository.saveDefinition(owner, baseline, request(), "save").mayHaveSaved)
        gateway.onUpdateDeploymentDefinition = { _, _, _ -> sends++; ApiResult.Success(baseline.copy(sourceKind = "javaJar")) }
        assertTrue(repository.saveDefinition(owner, baseline, request(), "save-2").mayHaveSaved)
        assertEquals(2, sends)
    }
    @Test fun `known permission failure and preflight transport failure remain distinct from uncertain mutation`() = runTest {
        val owner = login()
        gateway.onUpdateDeploymentDefinition = { _, _, _ -> sends++; ApiResult.Problem(403, "application-deployment.permission_denied", null) }
        val denied = repository.saveDefinition(owner, baseline, request(), "save")
        assertFalse(denied.mayHaveSaved); assertEquals(403, (denied.result as ApiResult.Problem).status)
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Transport(null) }
        assertFalse(repository.saveDefinition(owner, baseline, request(), "save-2").mayHaveSaved)
        assertEquals(1, sends)
    }
    @Test fun `login switch during preflight cancels before sending`() = runTest {
        val owner = login()
        gateway.onDeploymentSnapshot = { _, _, _ -> login(); ApiResult.Success(DeploymentSnapshot(baseline, emptyList(), emptyList(), null)) }
        assertTrue(runCatching { repository.saveDefinition(owner, baseline, request(), "save") }.exceptionOrNull() is CancellationException)
        assertEquals(0, sends)
    }
    @Test fun `auth retry rechecks baseline before the same mutation key can be sent`() = runTest {
        val owner = login()
        gateway.onRefresh = { _, _ -> actual = actual.copy(updatedAt = "2026-10-01T00:00:00.1234568+00:00"); ApiResult.Success(AuthTokens("next", "refresh-next", null, null)) }
        gateway.onUpdateDeploymentDefinition = { _, _, _ -> sends++; ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null) }
        val result = repository.saveDefinition(owner, baseline, request(), "save")
        assertEquals("application-deployment.definition_conflict", (result.result as ApiResult.Problem).code)
        assertEquals(1, sends); assertFalse(result.mayHaveSaved)
    }
    @Test fun `secret rotation readback verifies a new version and preserves null response bodies`() {
        val request = request().copy(configuration = baseline.configuration.map { if (it.isSecret) it.copy(value = "new-secret") else it })
        val receipt = baseline.copy(name = request.name, updatedAt = "2026-10-01T00:00:01Z", configuration = baseline.configuration.map { if (it.isSecret) it.copy(secretVersion = 8) else it })
        assertTrue(request.matchesReceipt(receipt, baseline))
        assertFalse(request.matchesReceipt(receipt.copy(configuration = baseline.configuration), baseline))
    }
}
