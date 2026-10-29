package app.relaxkonos.mobile.ui.manage.tailscale

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.*
import java.time.Instant

@OptIn(ExperimentalCoroutinesApi::class)
class TailscaleLoginViewModelTest {
    private val base = FakeGateway()
    private val session = AuthSession(base)
    private val storage = TailscaleControlTest.Storage()
    private val references = TailscaleOperationReferences(storage)
    private val operations = mutableMapOf<String,TailscaleLoginOperation>()
    private var executes = 0
    private var challenges = 0
    private var opened = 0
    private var invalidations = 0
    private var loseResponse = false
    private var challengeWait: CompletableDeferred<Unit>? = null
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun prepareTailscaleLogin(serverUrl: String, accessToken: String, id: String): ApiResult<TailscaleLoginOperation> {
            val plan = TailscaleLoginOperation(id,TailscaleLoginState.Prepared,"A".repeat(64),Instant.now().plusSeconds(240),Instant.now(),null,null)
            operations[id] = plan; return ApiResult.Success(plan)
        }
        override suspend fun executeTailscaleLogin(serverUrl: String, accessToken: String, id: String): ApiResult<TailscaleLoginOperation> {
            executes++; val result = operations.getValue(id).copy(state = TailscaleLoginState.Issued); operations[id] = result
            return if (loseResponse) ApiResult.Transport(null) else ApiResult.Success(result)
        }
        override suspend fun tailscaleLoginOperation(serverUrl: String, accessToken: String, id: String) = ApiResult.Success(operations.getValue(id))
        override suspend fun tailscaleLoginChallenge(serverUrl: String, accessToken: String, id: String): ApiResult<TailscaleLoginChallenge> {
            challenges++; challengeWait?.await()
            return ApiResult.Success(TailscaleLoginChallenge(id,TailscaleLoginTest.URL,operations.getValue(id).expiresAt))
        }
    }
    private val repository = TailscaleLoginRepository(gateway,session,
        ElevationRepository(gateway,session,CredentialVault(InMemoryVaultStorage(),FakeVaultCrypto()),UsageMemoryStore(InMemoryUsageMemoryStorage())),references)
    private val store = ViewModelStore()
    private lateinit var model: TailscaleLoginViewModel
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher()); model = TailscaleLoginViewModel(session,repository,ElevationAnswerProvider.Declines)
        store.put("tailscale-login",model); model.setActive(true)
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login(account: String = "alice") {
        base.onLogin = { _,_,_ -> ApiResult.Success(loginSession().let { it.copy(userName = account,server = it.server.copy(capabilities = setOf(ServerCapabilities.TAILSCALE),privilegedOperations = true)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"),account,"pw".toCharArray()) {}
    }
    @Test fun `opening page never issues login or reads a challenge`() = runTest {
        login(); advanceUntilIdle(); model.loadRecords(); advanceUntilIdle()
        assertTrue(model.canStart); assertNull(model.review); assertEquals(0,executes); assertEquals(0,challenges)
    }
    @Test fun `exact confirmation is required and browser requires its separate tap`() = runTest {
        login(); advanceUntilIdle(); model.prepare(); advanceUntilIdle()
        val review = requireNotNull(model.review); assertEquals(0,executes)
        model.submit(review) { invalidations++ }; advanceUntilIdle()
        assertEquals(1,executes); assertEquals(1,invalidations); assertEquals(0,challenges); assertTrue(model.canOpen)
        model.open { assertEquals(TailscaleLoginTest.URL,it); opened++; true }; advanceUntilIdle()
        assertEquals(1,opened); assertEquals(1,challenges); assertTrue(model.state.browserOpened)
        assertFalse(model.state.toString().contains("login.tailscale.com"))
    }
    @Test fun `dismissed or backgrounded confirmations cannot execute`() = runTest {
        login(); advanceUntilIdle(); model.prepare(); advanceUntilIdle()
        val review = requireNotNull(model.review); model.dismiss(); model.submit(review) {}; advanceUntilIdle()
        model.prepare(); advanceUntilIdle(); val next = requireNotNull(model.review)
        model.setActive(false); model.submit(next) {}; advanceUntilIdle()
        assertEquals(0,executes); assertNull(model.review)
    }
    @Test fun `account switch invalidates prepared confirmation and history`() = runTest {
        login(); advanceUntilIdle(); model.prepare(); advanceUntilIdle(); val review = requireNotNull(model.review)
        login("bob"); advanceUntilIdle(); model.submit(review) {}; model.loadRecords(); advanceUntilIdle()
        assertEquals(0,executes); assertTrue(model.state.records.isEmpty())
    }
    @Test fun `lost response only reopens original operation and never reexecutes`() = runTest {
        login(); advanceUntilIdle(); model.prepare(); advanceUntilIdle(); loseResponse = true
        model.submit(requireNotNull(model.review)) {}; advanceUntilIdle()
        assertFalse(model.canOpen); assertFalse(model.canStart)
        model.read(); advanceUntilIdle(); assertTrue(model.canOpen); assertEquals(1,executes)
    }
    @Test fun `backgrounding during challenge read prevents browser launch`() = runTest {
        login(); advanceUntilIdle(); model.prepare(); advanceUntilIdle(); model.submit(requireNotNull(model.review)) {}; advanceUntilIdle()
        challengeWait = CompletableDeferred(); model.open { opened++; true }; runCurrent()
        model.setActive(false); challengeWait!!.complete(Unit); advanceUntilIdle()
        assertEquals(0,opened); assertFalse(model.state.browserOpened)
    }
    @Test fun `leaving during challenge wait prevents late handoff`() = runTest {
        login(); advanceUntilIdle(); model.prepare(); advanceUntilIdle(); model.submit(requireNotNull(model.review)) {}; advanceUntilIdle()
        challengeWait = CompletableDeferred(); model.open { opened++; true }; runCurrent()
        model.stop(); challengeWait!!.complete(Unit); advanceUntilIdle()
        assertEquals(0,opened); assertNull(model.state.operation); assertFalse(model.state.busy)
    }
    @Test fun `corrupt operation references disable login instead of hiding unfinished tasks`() = runTest {
        login(); advanceUntilIdle(); storage.bytes = byteArrayOf(1,2,3)
        model.loadRecords(); advanceUntilIdle()
        assertTrue(model.state.storageFailed); assertFalse(model.canStart); assertEquals(0,executes)
    }
}
