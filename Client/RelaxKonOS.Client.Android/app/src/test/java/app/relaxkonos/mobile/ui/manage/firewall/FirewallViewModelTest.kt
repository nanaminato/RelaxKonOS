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
        model.change(facts, change); advanceUntilIdle()
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
}
