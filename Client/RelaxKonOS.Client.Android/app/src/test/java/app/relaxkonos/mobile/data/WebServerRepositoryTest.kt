package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class WebServerRepositoryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val storage = Storage()
    private val journal = WebServerRequestJournal(storage)
    private val index = OperationIndex(object : OperationIndexStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    })
    private val repository = WebServerRepository(gateway, session,
        ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto())), index, journal)
    private val server = WebPublishingWire.servers(WEB_SERVER).single()
    private val operation = WebPublishingWire.operation(WEB_OPERATION)
    private suspend fun signIn(): SessionState.Active {
        val login = loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.WEB_SERVER), privilegedOperations = true)) }
        gateway.onLogin = { _, _, _ -> ApiResult.Success(login) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `unknown request persists original key and replay queries facts first`() = runTest {
        val owner = signIn()
        var facts = false
        val keys = mutableListOf<String>()
        gateway.onWebLifecycle = { _, _, key -> keys += key; ApiResult.Transport(null) }
        repository.lifecycle(owner, server, WebServerAction.Reload, ElevationAnswerProvider.Declines)
        val pending = WebServerRequestJournal(storage).pending(owner).single()
        assertTrue(pending.attempted)
        gateway.onWebDiscover = { facts = true; ApiResult.Success(listOf(server)) }
        gateway.onWebLifecycle = { _, _, key -> assertTrue(facts); keys += key; ApiResult.Success(operation) }
        assertTrue(repository.resume(owner, pending, ElevationAnswerProvider.Declines) is ApiResult.Success)
        assertEquals(listOf(pending.key, pending.key), keys)
        assertTrue(journal.pending(owner).isEmpty())
        assertEquals(OperationDomain.WebServer, index.forOwner(owner).single().domain)
    }
    @Test fun `mismatched response never becomes the original operation`() = runTest {
        val owner = signIn()
        gateway.onWebLifecycle = { _, _, _ -> ApiResult.Success(operation.copy(instanceId = "other")) }
        assertTrue(repository.lifecycle(owner, server, WebServerAction.Reload, ElevationAnswerProvider.Declines) is ApiResult.Transport)
        assertTrue(index.forOwner(owner).isEmpty())
        assertEquals(1, journal.pending(owner).size)
    }
    @Test fun `owner change cancels before another network call`() = runTest {
        val owner = signIn()
        signIn()
        assertTrue(runCatching { repository.discover(owner) }.exceptionOrNull() is CancellationException)
    }
    @Test fun `definitive initial preflight refusal releases pending intent`() = runTest {
        val owner = signIn()
        gateway.onWebLifecycle = { _, _, _ -> ApiResult.Problem(400, "webserver.lifecycle_action_invalid", null) }
        assertTrue(repository.lifecycle(owner, server, WebServerAction.Reload, ElevationAnswerProvider.Declines) is ApiResult.Problem)
        assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `initial not found releases request but ambiguous replay keeps evidence`() = runTest {
        val owner = signIn()
        gateway.onWebLifecycle = { _, _, _ -> ApiResult.Problem(404, "", null) }
        repository.lifecycle(owner, server, WebServerAction.Reload, ElevationAnswerProvider.Declines)
        assertTrue(journal.pending(owner).isEmpty())
        gateway.onWebLifecycle = { _, _, _ -> ApiResult.Transport(null) }
        repository.lifecycle(owner, server, WebServerAction.Reload, ElevationAnswerProvider.Declines)
        val pending = journal.pending(owner).single()
        gateway.onWebDiscover = { ApiResult.Success(listOf(server)) }
        gateway.onWebLifecycle = { _, _, _ -> ApiResult.Problem(404, "", null) }
        repository.resume(owner, pending, ElevationAnswerProvider.Declines)
        assertEquals(pending, journal.pending(owner).single())
    }
    @Test fun `explicit integration fact acceptance clears only selected local recovery record`() = runTest {
        val owner = signIn()
        val integration = journal.begin(owner, server.id, "integrate")
        journal.update(integration.copy(attempted = true))
        val unresolved = journal.pending(owner).single()
        val other = journal.begin(owner, "another-instance", "reload")
        assertTrue(runCatching { repository.acceptIntegrationFacts(owner, other) }.isFailure)
        repository.acceptIntegrationFacts(owner, unresolved)
        assertEquals(listOf(other), journal.pending(owner))
        assertTrue(index.forOwner(owner).isEmpty())
    }
    @Test fun `different action cannot replace unresolved original key`() = runTest {
        val owner = signIn()
        val pending = journal.begin(owner, server.id, "reload")
        assertTrue(runCatching { journal.begin(owner, server.id, "stop") }.isFailure)
        assertEquals(pending.key, WebServerRequestJournal(storage).pending(owner).single().key)
    }
}
