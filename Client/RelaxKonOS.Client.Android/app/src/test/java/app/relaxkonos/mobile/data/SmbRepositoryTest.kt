package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class SmbRepositoryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway(); private val session = AuthSession(gateway); private val storage = Storage()
    private val journal = SmbMutationJournal(storage)
    private val elevation = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), app.relaxkonos.mobile.data.UsageMemoryStore(app.relaxkonos.mobile.data.InMemoryUsageMemoryStorage()))
    private val repository = SmbRepository(gateway, session, elevation, journal)
    private val facts = SmbFacts(SmbCapabilities(true, true, true, true, false, null),
        SmbStatus(SmbRuntimeState.Running, "4.20", true, true, null),
        listOf(SmbShare("opaque", "share", "/srv/data", null, false, true, false, emptyList(), true, false)),
        listOf(SmbUser("alice", true, true)), SmbConnection("host", 445, "prefix", "smb://host/"))
    private val receipt = SmbReceipt("11111111-1111-1111-1111-111111111111", true, null)
    private val change = SmbChange(SmbChangeKind.Password, "alice")
    private suspend fun signIn(manage: Boolean = true, account: String = "alice"): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(userName = account,
            server = it.server.copy(capabilities = setOf(ServerCapabilities.FILE_SERVICES), privilegedOperations = manage)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), account, "pw".toCharArray()) {}
        gateway.onSmbStatus = { ApiResult.Success(facts.status) }; gateway.onSmbCapabilities = { ApiResult.Success(facts.capabilities) }
        gateway.onSmbShares = { ApiResult.Success(facts.shares!!) }; gateway.onSmbUsers = { ApiResult.Success(facts.users!!) }
        gateway.onSmbConnection = { ApiResult.Success(facts.connection) }
        return session.state.value as SessionState.Active
    }
    @Test fun `lost write persists no body or secret and requires explicit adoption without replay`() = runTest {
        val owner = signIn(); var calls = 0; val secret = "private-secret".toCharArray()
        gateway.onSmbChange = { _, password -> calls++; assertEquals("private-secret", String(password!!)); ApiResult.Transport(null) }
        assertTrue(repository.change(owner, facts, change, secret, ElevationAnswerProvider.Declines) is ApiResult.Transport)
        assertTrue(secret.all { it == '\u0000' }); assertFalse(storage.bytes!!.decodeToString().contains("private-secret"))
        repository.facts(owner); val pending = SmbMutationJournal(storage).pending(owner).single()
        assertTrue(runCatching { repository.change(owner, facts, change, "private-secret".toCharArray(), ElevationAnswerProvider.Declines) }.isFailure)
        assertEquals(1, calls); assertTrue(repository.acceptFacts(owner, pending) is ApiResult.Success); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `changed share facts refuse write before journaling or exposing password`() = runTest {
        val owner = signIn(); gateway.onSmbShares = { ApiResult.Success(facts.shares!!.map { it.copy(drifted = true) }) }
        val password = "private-secret".toCharArray()
        assertEquals("file-services.smb.facts_changed", (repository.change(owner, facts, change, password, ElevationAnswerProvider.Declines) as ApiResult.Problem).code)
        assertTrue(password.all { it == '\u0000' }); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `SMB elevation uses exact capability target and rereads after authorization`() = runTest {
        val owner = signIn(); var calls = 0; val admin = "admin-secret".toCharArray()
        gateway.onSmbChange = { _, _ -> calls++; ApiResult.Problem(403, "file-services.smb.elevation_required", null) }
        gateway.onElevation = { _, _, capability, target, _, _ ->
            assertEquals("smbManage", capability); assertEquals("smb:managed", target)
            gateway.onSmbUsers = { ApiResult.Success(listOf(SmbUser("alice", false, true))) }; ApiResult.Success(ElevationGrant(true, null))
        }
        val result = repository.change(owner, facts, change, "private-secret".toCharArray(), ElevationAnswerProvider { _, _ -> ElevationAnswer("root", admin) })
        assertEquals("file-services.smb.facts_changed", (result as ApiResult.Problem).code); assertEquals(1, calls)
        assertTrue(admin.all { it == '\u0000' }); assertEquals(1, repository.pending(owner).size)
    }
    @Test fun `receipt stays unknown if post write facts fail and can later be adopted`() = runTest {
        val owner = signIn()
        gateway.onSmbChange = { _, _ -> gateway.onSmbShares = { ApiResult.Transport(null) }; ApiResult.Success(receipt) }
        assertTrue(repository.change(owner, facts, SmbChange(SmbChangeKind.Stop), null, ElevationAnswerProvider.Declines) is ApiResult.Success)
        val pending = repository.pending(owner).single(); assertEquals(receipt.operationId, pending.receiptId)
        assertTrue(repository.acceptFacts(owner, pending) is ApiResult.Transport); assertEquals(1, repository.pending(owner).size)
        gateway.onSmbShares = { ApiResult.Success(emptyList()) }
        assertTrue(repository.acceptFacts(owner, pending) is ApiResult.Success); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `only managed undrifted shares and eligible Samba accounts can change`() = runTest {
        val owner = signIn()
        listOf(facts.copy(shares = facts.shares!!.map { it.copy(managed = false) }), facts.copy(shares = facts.shares!!.map { it.copy(drifted = true) })).forEach {
            assertTrue(runCatching { repository.change(owner, it, SmbChange(SmbChangeKind.DeleteShare, "opaque"), null, ElevationAnswerProvider.Declines) }.isFailure)
        }
        assertTrue(runCatching { repository.change(owner, facts.copy(users = listOf(SmbUser("alice", true, false))), change, "private-secret".toCharArray(), ElevationAnswerProvider.Declines) }.isFailure)
        assertTrue(runCatching { repository.change(owner, facts.copy(capabilities = facts.capabilities.copy(sambaCredentialsSupported = false)), change, "private-secret".toCharArray(), ElevationAnswerProvider.Declines) }.isFailure)
        assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `unsupported state does not query unavailable collections but install support remains visible`() = runTest {
        val owner = signIn(); gateway.onSmbStatus = { ApiResult.Success(facts.status.copy(state = SmbRuntimeState.NotInstalled)) }
        gateway.onSmbShares = { error("No collection query before installation") }; gateway.onSmbUsers = { error("No user query before installation") }
        val result = (repository.facts(owner) as ApiResult.Success).value
        assertNull(result.shares); assertNull(result.users); assertTrue(result.capabilities.installSupported)
        val observer = signIn(false); assertTrue(runCatching { repository.change(observer, facts, SmbChange(SmbChangeKind.Stop), null, ElevationAnswerProvider.Declines) }.isFailure)
    }
    @Test fun `switched sessions and corrupt storage refuse original writes`() = runTest {
        val owner = signIn(); storage.bytes = byteArrayOf(1, 2)
        assertTrue(runCatching { repository.change(owner, facts, SmbChange(SmbChangeKind.Stop), null, ElevationAnswerProvider.Declines) }.isFailure)
        storage.bytes = null; signIn(account = "bob")
        assertTrue(runCatching { repository.facts(owner) }.exceptionOrNull() is CancellationException)
    }
    @Test fun `cancelling while awaiting mutation lock still zeroes password`() = runTest {
        val owner = signIn(); val prompt = CompletableDeferred<Unit>(); val answer = CompletableDeferred<ElevationAnswer?>()
        gateway.onSmbChange = { _, _ -> ApiResult.Problem(403, "file-services.smb.elevation_required", null) }
        val first = launch { repository.change(owner, facts, SmbChange(SmbChangeKind.Stop), null, ElevationAnswerProvider { _, _ -> prompt.complete(Unit); answer.await() }) }
        prompt.await(); val password = "waiting-secret".toCharArray()
        val waiting = launch { repository.change(owner, facts, change, password, ElevationAnswerProvider.Declines) }
        yield(); waiting.cancelAndJoin(); assertTrue(password.all { it == '\u0000' }); first.cancelAndJoin()
        assertEquals(1, repository.pending(owner).size)
    }
    @Test fun `successful synchronous receipt and reread clear marker without inventing remote task`() = runTest {
        val owner = signIn(); gateway.onSmbChange = { _, _ -> ApiResult.Success(receipt) }
        assertEquals(receipt, (repository.change(owner, facts, SmbChange(SmbChangeKind.Stop), null, ElevationAnswerProvider.Declines) as ApiResult.Success).value)
        assertTrue(repository.pending(owner).isEmpty())
    }
}
