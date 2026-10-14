package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class DockerComposeRecoveryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        var failWrite = false
        override fun read() = bytes
        override fun write(bytes: ByteArray) { if (failWrite) error("storage failure"); this.bytes = bytes }
    }
    private val storage = Storage()
    private val base = FakeGateway()
    private var writes = 0
    private val queries = mutableListOf<String>()
    private var response: ApiResult<DockerStackOperation> = ApiResult.Transport(null)
    private var cancelled = false
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun dockerStackDeploy(serverUrl: String, accessToken: String, name: String, composeYaml: String, definitionVersion: String, idempotencyKey: String): ApiResult<DockerStackOperation> {
            writes++
            assertEquals(idempotencyKey, journal.pending(owner).single().requestKey)
            assertFalse(storage.bytes!!.decodeToString().contains("private yaml"))
            if (cancelled) throw CancellationException("response lost")
            return ApiResult.Transport(null)
        }
        override suspend fun dockerStackOperationByRequest(serverUrl: String, accessToken: String, requestKey: String): ApiResult<DockerStackOperation> {
            queries += requestKey; return response
        }
    }
    private val session = AuthSession(gateway)
    private val journal = DockerComposeJournal(storage)
    private val index = OperationIndex(object : OperationIndexStorage {
        var data: ByteArray? = null
        override fun read() = data
        override fun write(bytes: ByteArray) { data = bytes }
    })
    private lateinit var owner: SessionState.Active
    private fun repository(journal: DockerComposeJournal = this.journal) = DockerRepository(gateway, session, index, testDockerGate(compose = journal))
    private suspend fun login() {
        base.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.DOCKER), privilegedOperations = true)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://compose-review.invalid"), "review", "test".toCharArray()) {}
        owner = session.state.value as SessionState.Active
    }
    private val operation = DockerStackOperation("11111111-1111-1111-1111-111111111111", "review", DockerStackOperationKind.DEPLOY,
        DockerStackOperationState.SUCCEEDED, "completed", null, null, "2026-10-14T00:00:00Z", false, emptyList())
    @Test fun `response loss persists original identity and blocks a new submission`() = runTest {
        login(); val repo = repository()
        repo.deployStack(owner, "review", "private yaml", "version")
        val record = journal.pending(owner).single()
        assertEquals(record, DockerComposeJournal(storage).pending(owner).single())
        assertTrue(repo.deployStack(owner, "review", "private yaml", "version") is ApiResult.Problem)
        assertEquals(1, writes)
    }
    @Test fun `cancelled submission preserves pending identity`() = runTest {
        login(); cancelled = true
        try { repository().deployStack(owner, "review", "private yaml", "version"); fail() } catch (_: CancellationException) {}
        assertEquals(1, writes); assertEquals(1, journal.pending(owner).size)
    }
    @Test fun `not found and transport never clear uncertainty or replay a write`() = runTest {
        login(); val repo = repository(); repo.deployStack(owner, "review", "private yaml", "version")
        val record = journal.pending(owner).single()
        response = ApiResult.Problem(404, "", null); repo.recoverRequest(owner, record)
        response = ApiResult.Transport(null); repo.recoverRequest(owner, record)
        assertEquals(listOf(record.requestKey, record.requestKey), queries)
        assertEquals(listOf(record), journal.pending(owner)); assertEquals(1, writes)
    }
    @Test fun `positive exact lookup after reopening records operation before clearing pending`() = runTest {
        login(); repository().deployStack(owner, "review", "private yaml", "version")
        val reopened = DockerComposeJournal(storage); val record = reopened.pending(owner).single()
        response = ApiResult.Success(operation)
        assertTrue(repository(reopened).recoverRequest(owner, record) is ApiResult.Success)
        assertTrue(reopened.pending(owner).isEmpty()); assertEquals(operation.operationId, index.forOwner(owner).single().operationId)
        assertEquals(listOf(record.requestKey), queries); assertEquals(1, writes)
    }
    @Test fun `mismatched lookup cannot erase pending or index another project`() = runTest {
        login(); val repo = repository(); repo.deployStack(owner, "review", "private yaml", "version")
        response = ApiResult.Success(operation.copy(projectName = "different"))
        try { repo.recoverRequest(owner, journal.pending(owner).single()); fail() } catch (_: IllegalArgumentException) {}
        assertEquals(1, journal.pending(owner).size); assertTrue(index.forOwner(owner).isEmpty())
    }
    @Test fun `journal write failure prevents any remote submission`() = runTest {
        login(); storage.failWrite = true
        try { repository().deployStack(owner, "review", "private yaml", "version"); fail() } catch (_: IllegalStateException) {}
        assertEquals(0, writes)
    }
}
