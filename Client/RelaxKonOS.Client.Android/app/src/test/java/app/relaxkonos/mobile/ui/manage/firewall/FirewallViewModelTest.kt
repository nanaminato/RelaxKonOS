package app.relaxkonos.mobile.ui.manage.firewall

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Before
import org.junit.After
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class FirewallViewModelTest {
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val journal = FirewallMutationJournal(Storage())
    private val repository = FirewallRepository(gateway, session,
        ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage())), journal)
    private val status = FirewallStatus(true, false, "ufw", null, "deny", "allow", "")
    private val facts = FirewallFacts(status, emptyList())
    private val change = FirewallChange(FirewallChangeKind.Enabled, enabled = true)
    private val store = ViewModelStore()
    private lateinit var model: FirewallViewModel
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = FirewallViewModel(session, repository, ElevationAnswerProvider.Declines)
        store.put("firewall", model)
        gateway.onFirewallStatus = { ApiResult.Success(status) }
        gateway.onFirewallRules = { ApiResult.Success(emptyList()) }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.FIREWALL), privilegedOperations = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://firewall-review.invalid"), "review", "test".toCharArray()) {}
    }
    @Test fun `cancelled refresh revokes old facts and explicit refresh recovers`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts)
        gateway.onFirewallStatus = { throw CancellationException("private cancelled read") }
        model.refresh(); advanceUntilIdle()
        assertNull(model.state.facts); assertFalse(model.state.busy)
        assertEquals("firewall.unverified", model.state.problem)
        gateway.onFirewallStatus = { ApiResult.Success(status) }
        model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts); assertNull(model.state.problem)
    }
    @Test fun `cancelled write exposes its recovery marker without replay`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        var sends = 0
        gateway.onChangeFirewall = { sends++; throw CancellationException("private cancelled write") }
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        model.change(requireNotNull(model.confirmation)); advanceUntilIdle()
        assertNull(model.state.facts); assertFalse(model.state.busy)
        assertEquals("firewall.unverified", model.state.problem)
        assertEquals(1, model.state.pending.size)
        model.refresh(); advanceUntilIdle()
        assertEquals(facts, model.state.facts); assertEquals(1, model.state.pending.size)
        assertEquals(1, sends)
    }
    @Test fun `failed adoption is not replaced with a second successful read`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val pending = journal.begin(requireNotNull(model.state.owner), change.kind, change.number)
        var reads = 0
        gateway.onFirewallStatus = { reads++; if (reads == 1) ApiResult.Transport(null) else ApiResult.Success(status) }
        model.accept(pending); advanceUntilIdle()
        assertNull(model.state.facts)
        assertEquals("firewall.unverified", model.state.problem)
        assertEquals(listOf(pending), model.state.pending); assertEquals(1, reads)
    }
    private suspend fun TestScope.review(): FirewallConfirmation {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        return requireNotNull(model.confirmation)
    }
    @Test fun `old login confirmation cannot write identical facts after relogin`() = runTest {
        val old = review(); var sends = 0
        gateway.onChangeFirewall = { sends++; ApiResult.Transport(null) }
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.change(old); advanceUntilIdle(); assertEquals(0, sends)
        assertTrue(model.state.pending.isEmpty())
    }
    @Test fun `refresh and dismissal invalidate confirmation even after identical review reopens`() = runTest {
        val old = review(); var sends = 0
        gateway.onChangeFirewall = { sends++; ApiResult.Transport(null) }
        model.refresh(); advanceUntilIdle(); assertNull(model.confirmation)
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        val fresh = requireNotNull(model.confirmation)
        model.change(old); advanceUntilIdle(); assertEquals(0, sends)
        model.dismissConfirmation(); model.change(fresh); advanceUntilIdle(); assertEquals(0, sends)
    }
    @Test fun `confirmation is consumed only once and stop invalidates it`() = runTest {
        val old = review(); model.stop(); model.change(old); advanceUntilIdle()
        model.refresh(); advanceUntilIdle()
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        val fresh = requireNotNull(model.confirmation); var sends = 0
        gateway.onChangeFirewall = { sends++; ApiResult.Transport(null) }
        model.change(fresh); model.change(fresh); advanceUntilIdle()
        assertEquals(1, sends); assertEquals(1, model.state.pending.size)
    }
    @Test fun `stopped read cannot republish old facts after a new refresh`() = runTest {
        login(); runCurrent()
        lateinit var reply: kotlin.coroutines.Continuation<ApiResult<List<FirewallRule>>>
        gateway.onFirewallRules = { kotlin.coroutines.suspendCoroutine { reply = it } }
        model.refresh(); runCurrent()
        assertTrue(model.state.busy)
        model.stop()
        val latest = status.copy(defaultIncomingPolicy = "allow")
        gateway.onFirewallStatus = { ApiResult.Success(latest) }
        gateway.onFirewallRules = { ApiResult.Success(emptyList()) }
        model.refresh(); runCurrent()
        reply.resumeWith(Result.success(ApiResult.Success(emptyList())))
        advanceUntilIdle()
        assertEquals(latest, model.state.facts?.status)
        assertFalse(model.state.busy)
        assertNull(model.state.problem)
    }
    @Test fun `successful change reuses repository verification without another read`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        var reads = 0; var writes = 0
        gateway.onFirewallStatus = { reads++; check(reads <= 3) { "redundant read" }; ApiResult.Success(status) }
        gateway.onChangeFirewall = { writes++; ApiResult.Success(FirewallResult(true, "")) }
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        model.change(requireNotNull(model.confirmation)); advanceUntilIdle()
        assertEquals(3, reads); assertEquals(1, writes)
        assertEquals(facts, model.state.facts); assertNull(model.state.problem)
        assertEquals(1, model.state.saved); assertTrue(model.state.pending.isEmpty())
    }
    @Test fun `known successful change with failed verification closes submitted draft and retains gate`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        var writes = 0
        gateway.onChangeFirewall = { writes++; gateway.onFirewallStatus = { error("private post-write verification failure") }; ApiResult.Success(FirewallResult(true, "")) }
        model.requestChange(requireNotNull(model.state.owner), facts, change)
        model.change(requireNotNull(model.confirmation)); advanceUntilIdle()
        assertEquals(1, model.state.saved); assertNull(model.state.facts)
        assertEquals("firewall.refresh_failed", model.state.problem)
        assertEquals(1, model.state.pending.size); assertEquals(1, writes)
        assertFalse(model.state.busy)
    }

}
