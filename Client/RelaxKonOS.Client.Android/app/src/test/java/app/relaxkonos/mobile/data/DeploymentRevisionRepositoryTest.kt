package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class DeploymentRevisionRepositoryTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val repository = DeploymentRepository(gateway, session)
    private val baseline = deploymentFixture()
    private val input = DeploymentRevisionSource(imageReference = "nginx:1.27.3-alpine", arguments = listOf("", " exact value "))
    private val operation = DeploymentOperation("d3708cc7-3e7e-42ad-b498-11466a48af24", baseline.id, "deploy", "queued", "preflight", null, null, null, 0, true)
    private var snapshot = DeploymentSnapshot(baseline, emptyList(), emptyList(), null)
    private var sends = 0
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://host.test"), "alice", "p".toCharArray()) {}
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(snapshot) }
        gateway.onDeployRevision = { id, source, version, key ->
            sends++; assertEquals(baseline.id, id); assertEquals(input, source); assertEquals(baseline.updatedAt, version); assertEquals("revision-key", key)
            ApiResult.Success(operation)
        }
        return session.state.value as SessionState.Active
    }
    @Test fun `existing revision retains exact definition and submits only independent input`() = runTest {
        val owner = login()
        assertEquals(operation, (repository.deployRevision(owner, baseline, input, "revision-key").result as ApiResult.Success).value)
        assertEquals(1, sends); assertEquals(baseline, snapshot.application); assertEquals(0, gateway.elevationCount)
    }
    @Test fun `stale ticks or active operation block submission`() = runTest {
        val owner = login()
        snapshot = snapshot.copy(application = baseline.copy(updatedAt = "2026-10-01T00:00:00.1234568+00:00"))
        assertEquals("application-deployment.definition_conflict", (repository.deployRevision(owner, baseline, input, "revision-key").result as ApiResult.Problem).code)
        snapshot = snapshot.copy(application = baseline, activeOperation = operation)
        assertEquals("application-deployment.resource_conflict", (repository.deployRevision(owner, baseline, input, "revision-key").result as ApiResult.Problem).code)
        assertEquals(0, sends)
    }
    @Test fun `lost response blocks another submission until explicit inactive facts are adopted`() = runTest {
        val owner = login()
        gateway.onDeployRevision = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        assertTrue(repository.deployRevision(owner, baseline, input, "revision-key").mayHaveQueued)
        assertTrue(repository.deployRevision(owner, baseline, input, "another-key").mayHaveQueued); assertEquals(1, sends)
        repository.snapshot(owner, baseline.id)
        assertTrue(repository.hasUncertainRevision(owner, baseline.id))
        snapshot = snapshot.copy(activeOperation = operation)
        repository.reconcileRevision(owner, baseline.id)
        assertTrue(repository.hasUncertainRevision(owner, baseline.id))
        snapshot = snapshot.copy(activeOperation = null)
        repository.reconcileRevision(owner, baseline.id)
        assertFalse(repository.hasUncertainRevision(owner, baseline.id)); assertEquals(1, sends)
    }
    @Test fun `wrong receipt is unknown and a preflight transport error is never marked as submitted`() = runTest {
        val owner = login()
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Transport(null) }
        assertFalse(repository.deployRevision(owner, baseline, input, "revision-key").mayHaveQueued); assertEquals(0, sends)
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(snapshot) }
        gateway.onDeployRevision = { _, _, _, _ -> sends++; ApiResult.Success(operation.copy(applicationId = "wrong")) }
        assertTrue(repository.deployRevision(owner, baseline, input, "revision-key").mayHaveQueued); assertEquals(1, sends)
    }
    @Test fun `401 retry repeats baseline check and login change prevents sending`() = runTest {
        val owner = login()
        gateway.onDeployRevision = { _, _, _, _ -> sends++; ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null) }
        gateway.onRefresh = { _, _ -> snapshot = snapshot.copy(application = baseline.copy(updatedAt = "2026-10-01T00:00:00.1234568Z")); ApiResult.Success(AuthTokens("next", "refresh", null, null)) }
        assertEquals("application-deployment.definition_conflict", (repository.deployRevision(owner, baseline, input, "revision-key").result as ApiResult.Problem).code)
        assertEquals(1, sends)
        snapshot = snapshot.copy(application = baseline)
        gateway.onDeploymentSnapshot = { _, _, _ -> login(); ApiResult.Success(snapshot) }
        assertTrue(runCatching { repository.deployRevision(owner, baseline, input, "revision-key") }.exceptionOrNull() is CancellationException)
        assertEquals(1, sends)
    }
    private fun preview() = CatalogApplicationUpdatePreview(baseline.id, baseline.updatedAt, "revision-id", "1.0.0",
        CatalogTemplate("1", "personal-site", "2.0.0", "RelaxKonOS", "built-in", true, "website", "test", listOf("linux/amd64"), emptyList(), CatalogResources(1.0, 536870912, 512), emptyList(), emptyList(), 8080, "/", "retain data", false),
        "nginx:1.26", "nginx:1.27", "review migration", emptyList())
    @Test fun `catalog update submits reviewed exact version and discards changed preview`() = runTest {
        val owner = login(); val reviewed = preview()
        gateway.onPreviewCatalogUpdate = { _, _ -> ApiResult.Success(reviewed) }
        gateway.onUpdateCatalog = { actual, key -> assertEquals(reviewed, actual); assertEquals("catalog-key", key); sends++; ApiResult.Success(operation) }
        assertTrue(repository.updateCatalog(owner, baseline, reviewed, "catalog-key").result is ApiResult.Success); assertEquals(1, sends)
        gateway.onPreviewCatalogUpdate = { _, _ -> ApiResult.Success(reviewed.copy(updateNotes = "changed")) }
        assertEquals("application-deployment.definition_conflict", (repository.updateCatalog(owner, baseline, reviewed, "catalog-key").result as ApiResult.Problem).code)
        assertEquals(1, sends)
    }
    @Test fun `untrusted withdrawn blocked or failed preview never queues template update`() = runTest {
        val owner = login(); val reviewed = preview()
        assertTrue(repository.updateCatalog(owner, baseline, reviewed.copy(target = reviewed.target.copy(trusted = false)), "catalog-key").result is ApiResult.Problem)
        assertTrue(repository.updateCatalog(owner, baseline, reviewed.copy(blockers = listOf("unknown")), "catalog-key").result is ApiResult.Problem)
        gateway.onPreviewCatalogUpdate = { _, _ -> ApiResult.Transport(null) }
        assertFalse(repository.updateCatalog(owner, baseline, reviewed, "catalog-key").mayHaveQueued)
        assertEquals(0, sends)
    }
}
