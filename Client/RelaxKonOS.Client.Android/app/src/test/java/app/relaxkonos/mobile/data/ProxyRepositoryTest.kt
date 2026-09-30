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

class ProxyRepositoryTest {
    private class Storage : InstallationRequestStorage { var bytes: ByteArray? = null; override fun read() = bytes; override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() } }
    private val gateway = FakeGateway(); private val session = AuthSession(gateway); private val storage = Storage()
    private val journal = ProxyRequestJournal(storage)
    private val index = OperationIndex(object : OperationIndexStorage { var bytes: ByteArray? = null; override fun read() = bytes; override fun write(bytes: ByteArray) { this.bytes = bytes } })
    private val repository = ProxyRepository(gateway, session, index, journal)
    private val id = "11111111-1111-1111-1111-111111111111"
    private suspend fun signIn(account: String = "alice", manage: Boolean = true): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(userName = account, server = it.server.copy(capabilities = setOf(ServerCapabilities.PROXY), privilegedOperations = manage)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), account, "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private fun facts() {
        gateway.onProxyOverview = { ApiResult.Success(ProxyOverview(ProxyRuntime("managed", ProxyRuntimeState.Running, "1", null, true, ""), null, false, true, "healthy", "", true, true, false, "linux", false)) }
        gateway.onProxyProfiles = { ApiResult.Success(emptyList()) }; gateway.onProxySubscriptions = { ApiResult.Success(emptyList()) }
    }
    @Test fun `lost queue response replays exact key only explicitly and accepted ID survives read failure`() = runTest {
        val owner = signIn(); val keys = mutableListOf<String>(); facts()
        gateway.onProxyQueue = { _, _, key -> keys += key; ApiResult.Transport(null) }
        assertTrue(repository.queue(owner, ProxyAction.Start) is ApiResult.Transport)
        val pending = ProxyRequestJournal(storage).pending(owner).single()
        assertTrue(runCatching { repository.queue(owner, ProxyAction.Start) }.isFailure); assertEquals(1, keys.size)
        gateway.onProxyQueue = { action, target, key -> assertEquals(ProxyAction.Start, action); assertNull(target); keys += key; ApiResult.Success(id) }
        gateway.onProxyOperation = { ApiResult.Transport(null) }
        assertTrue(repository.resume(owner, pending) is ApiResult.Transport)
        assertEquals(listOf(pending.key, pending.key), keys); assertTrue(journal.pending(owner).isEmpty())
        assertEquals(id, index.forOwner(owner).single().operationId)
    }
    @Test fun `lost subscription import stores no URL and reading facts does not silently resolve marker`() = runTest {
        val owner = signIn(); facts(); gateway.onImportProxySubscription = { ApiResult.Transport(null) }
        assertTrue(repository.import(owner, ProxyImportRequest("https://example.test/private-secret", "private-name", ProxyDownloadRoute.Direct)) is ApiResult.Transport)
        assertFalse(storage.bytes!!.decodeToString().contains("private-"))
        repository.profiles(owner); assertEquals(1, journal.pending(owner).size)
        assertTrue(repository.acceptFacts(owner, journal.pending(owner).single()) is ApiResult.Success); assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `stale response and observer mutation cannot cross session boundary`() = runTest {
        val owner = signIn(); gateway.onProxyOverview = { signIn("bob"); ApiResult.Transport(null) }
        assertTrue(runCatching { repository.overview(owner) }.exceptionOrNull() is CancellationException)
        assertTrue(repository.pending(session.state.value as SessionState.Active).isEmpty())
        val observer = signIn(manage = false)
        assertTrue(runCatching { repository.queue(observer, ProxyAction.Start) }.isFailure); assertTrue(journal.pending(observer).isEmpty())
    }
    @Test fun `malformed journal prevents unsafe replacement and wrong operation ID cannot enter index`() = runTest {
        val owner = signIn(); storage.bytes = byteArrayOf(1, 2, 3)
        assertTrue(runCatching { repository.queue(owner, ProxyAction.Start) }.isFailure)
        storage.bytes = null; gateway.onProxyOperation = { ApiResult.Success(ProxyOperation("22222222-2222-2222-2222-222222222222", "lifecycle.start", ProxyOperationState.Succeeded, "completed", "")) }
        assertTrue(repository.operation(owner, id) is ApiResult.Transport); assertTrue(index.forOwner(owner).isEmpty())
    }
    @Test fun `emergency recovery preserves unresolved writes and replays even when overview is unavailable`() = runTest {
        val owner = signIn(); gateway.onImportProxySubscription = { ApiResult.Transport(null) }
        repository.import(owner, ProxyImportRequest("https://example.test/secret", null, ProxyDownloadRoute.Direct))
        val original = journal.pending(owner).single(); val keys = mutableListOf<String>()
        gateway.onProxyQueue = { action, _, key -> assertEquals(ProxyAction.EmergencyDisableTun, action); keys += key; ApiResult.Transport(null) }
        assertTrue(repository.queue(owner, ProxyAction.EmergencyDisableTun) is ApiResult.Transport)
        val emergency = journal.pending(owner).single { it.action == ProxyAction.EmergencyDisableTun }
        assertTrue(runCatching { repository.queue(owner, ProxyAction.EmergencyDisableTun) }.isFailure)
        gateway.onProxyOverview = { error("Emergency replay must not depend on a healthy overview") }
        gateway.onProxyQueue = { _, _, key -> keys += key; ApiResult.Success(id) }
        gateway.onProxyOperation = { ApiResult.Success(ProxyOperation(id, ProxyAction.EmergencyDisableTun.kind, ProxyOperationState.Succeeded, "completed", "")) }
        assertTrue(repository.resume(owner, emergency) is ApiResult.Success)
        assertEquals(listOf(emergency.key, emergency.key), keys); assertEquals(listOf(original), journal.pending(owner))
    }
    @Test fun `selection marker cannot clear until controller facts are available`() = runTest {
        val owner = signIn(); facts(); val pending = journal.begin(owner, null, null, ProxyWrite.Selection)
        gateway.onProxyGroups = { ApiResult.Transport(null) }
        assertTrue(repository.acceptFacts(owner, pending) is ApiResult.Transport); assertEquals(1, journal.pending(owner).size)
        gateway.onProxyGroups = { ApiResult.Success(emptyList()) }
        assertTrue(repository.acceptFacts(owner, pending) is ApiResult.Success); assertTrue(journal.pending(owner).isEmpty())
    }
}
