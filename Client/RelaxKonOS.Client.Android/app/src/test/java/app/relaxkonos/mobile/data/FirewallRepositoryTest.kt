package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.launch
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.yield
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class FirewallRepositoryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway(); private val session = AuthSession(gateway); private val storage = Storage()
    private val journal = FirewallMutationJournal(storage)
    private val elevations = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), app.relaxkonos.mobile.data.UsageMemoryStore(app.relaxkonos.mobile.data.InMemoryUsageMemoryStorage()))
    private val repository = FirewallRepository(gateway, session, elevations, journal)
    private val status = FirewallStatus(true, false, "ufw", null, "deny", "allow", "")
    private val facts = FirewallFacts(status, emptyList())
    private val change = FirewallChange(FirewallChangeKind.Enabled, enabled = true)
    private suspend fun signIn(manage: Boolean = true, account: String = "alice"): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(userName = account, server = it.server.copy(capabilities = setOf(ServerCapabilities.FIREWALL), privilegedOperations = manage)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), account, "pw".toCharArray()) {}
        gateway.onFirewallStatus = { ApiResult.Success(status) }; gateway.onFirewallRules = { ApiResult.Success(emptyList()) }
        return session.state.value as SessionState.Active
    }
    @Test fun `lost mutation persists no secrets forbids replay and facts need explicit adoption`() = runTest {
        val owner = signIn(); var calls = 0
        gateway.onChangeFirewall = { _ -> calls++; ApiResult.Transport(null) }
        assertTrue(repository.change(owner, facts, change, ElevationAnswerProvider.Declines) is ApiResult.Transport)
        assertFalse(storage.bytes!!.decodeToString().contains("password"))
        repository.facts(owner); val pending = FirewallMutationJournal(storage).pending(owner).single()
        assertTrue(runCatching { repository.change(owner, facts, change, ElevationAnswerProvider.Declines) }.isFailure)
        assertEquals(1, calls)
        assertTrue(repository.acceptFacts(owner, pending) is ApiResult.Success); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `changed rule numbering or policy refuses mutation before authorization`() = runTest {
        val owner = signIn(); gateway.onFirewallStatus = { ApiResult.Success(status.copy(isEnabled = true)) }
        val result = repository.change(owner, facts, change, ElevationAnswerProvider.Declines)
        assertEquals("firewall.facts_changed", (result as ApiResult.Problem).code)
        assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `domain elevation refusal retries exact target with one administrator authentication`() = runTest {
        val owner = signIn(); val admin = "admin-secret".toCharArray(); var count = 0
        gateway.onChangeFirewall = { submitted ->
            assertEquals(change, submitted); count++
            ApiResult.Success(if (count == 1) FirewallResult(false, "firewall.elevation_required") else FirewallResult(true, ""))
        }
        gateway.onElevation = { _, _, capability, target, password, account ->
            assertEquals("firewallChange", capability); assertEquals("host/firewall", target)
            assertEquals("alice", account); assertEquals("admin-secret", String(password!!))
            ApiResult.Success(ElevationGrant(true, null))
        }
        assertTrue(repository.change(owner, facts, change, ElevationAnswerProvider { _, _ -> ElevationAnswer("alice", admin) }) is ApiResult.Success)
        assertEquals(2, count); assertTrue(admin.all { it == '\u0000' }); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `snapshot is reread after elevation prompt before numbered writes`() = runTest {
        val owner = signIn(); var calls = 0
        gateway.onChangeFirewall = { _ -> calls++; ApiResult.Success(FirewallResult(false, "firewall.elevation_required")) }
        gateway.onElevation = { _, _, _, _, _, _ -> gateway.onFirewallStatus = { ApiResult.Success(status.copy(isEnabled = true)) }; ApiResult.Success(ElevationGrant(true, null)) }
        val result = repository.change(owner, facts, change, ElevationAnswerProvider { _, _ -> ElevationAnswer("alice", "pw".toCharArray()) })
        assertEquals("firewall.facts_changed", (result as ApiResult.Problem).code); assertEquals(1, calls)
    }
    @Test fun `unavailable backend does not invent empty verified rules and observers cannot mutate`() = runTest {
        val owner = signIn(); gateway.onFirewallStatus = { ApiResult.Success(status.copy(isAvailable = false, problemCode = "firewall.not_supported")) }
        gateway.onFirewallRules = { error("Unavailable backend cannot read rules") }
        assertFalse((repository.facts(owner) as ApiResult.Success).value.status.isAvailable)
        val observer = signIn(manage = false)
        assertTrue(runCatching { repository.change(observer, facts, change, ElevationAnswerProvider.Declines) }.isFailure)
        assertTrue(repository.pending(observer).isEmpty())
    }
    @Test fun `corrupt journal and switched sessions cannot discard unknown original changes`() = runTest {
        val owner = signIn(); storage.bytes = byteArrayOf(1, 2)
        assertTrue(runCatching { repository.change(owner, facts, change, ElevationAnswerProvider.Declines) }.isFailure)
        storage.bytes = null; signIn(account = "bob")
        assertTrue(runCatching { repository.facts(owner) }.exceptionOrNull() is CancellationException)
    }
    @Test fun `cancelling a waiting mutation cannot replay an uncertain change`() = runTest {
        val owner = signIn(); val prompt = CompletableDeferred<Unit>(); val neverAnswered = CompletableDeferred<ElevationAnswer?>()
        gateway.onChangeFirewall = { _ -> ApiResult.Success(FirewallResult(false, "firewall.elevation_required")) }
        val first = launch { repository.change(owner, facts, change, ElevationAnswerProvider { _, _ -> prompt.complete(Unit); neverAnswered.await() }) }
        prompt.await()
        val waiting = launch { repository.change(owner, facts, change, ElevationAnswerProvider.Declines) }
        yield(); waiting.cancelAndJoin()
        first.cancelAndJoin()
        assertEquals(1, repository.pending(owner).size)
    }
}
