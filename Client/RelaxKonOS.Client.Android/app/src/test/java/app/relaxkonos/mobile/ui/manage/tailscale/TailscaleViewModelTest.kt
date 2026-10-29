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
class TailscaleViewModelTest {
    private val base = FakeGateway()
    private val session = AuthSession(base)
    private val storage = TailscaleControlTest.Storage()
    private val references = TailscaleOperationReferences(storage)
    private val baseline = TailscaleControl("A".repeat(64),false,true,TailscaleConfiguration(true,false),true,null)
    private var executions = 0
    private var loseResponse = false
    private var prepareHold: CompletableDeferred<Unit>? = null
    private val plans = mutableMapOf<String,TailscaleOperation>()
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun tailscaleControl(serverUrl: String, accessToken: String) = ApiResult.Success(baseline)
        override suspend fun prepareTailscale(serverUrl: String, accessToken: String, request: TailscalePlanRequest): ApiResult<TailscaleOperation> {
            prepareHold?.await()
            val plan = TailscaleOperation(request.operationId,request.action,TailscaleOperationState.Prepared,request.expectedRevision,Instant.now().plusSeconds(240),Instant.now(),request.configuration,null,null)
            plans[plan.operationId] = plan
            return ApiResult.Success(plan)
        }
        override suspend fun executeTailscale(serverUrl: String, accessToken: String, id: String): ApiResult<TailscaleOperation> {
            executions++; val result = plans.getValue(id).copy(state = TailscaleOperationState.Applied); plans[id] = result
            return if (loseResponse) ApiResult.Transport(null) else ApiResult.Success(result)
        }
        override suspend fun tailscaleOperation(serverUrl: String, accessToken: String, id: String) = ApiResult.Success(plans.getValue(id))
    }
    private val repository = TailscaleControlRepository(gateway,session,
        ElevationRepository(gateway,session,CredentialVault(InMemoryVaultStorage(),FakeVaultCrypto()),UsageMemoryStore(InMemoryUsageMemoryStorage())),references)
    private val store = ViewModelStore()
    private lateinit var model: TailscaleViewModel
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = TailscaleViewModel(session,repository,ElevationAnswerProvider.Declines)
        store.put("tailscale",model); model.setActive(true)
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun signIn(account: String = "alice") {
        base.onLogin = { _,_,_ -> ApiResult.Success(loginSession().let { it.copy(userName = account,server = it.server.copy(capabilities = setOf(ServerCapabilities.TAILSCALE),privilegedOperations = true)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"),account,"pw".toCharArray()) {}
    }
    @Test fun `opening only reads local references and never requests control authorization`() = runTest {
        signIn(); advanceUntilIdle(); model.loadRecords(); advanceUntilIdle()
        assertNull(model.state.control); assertNull(model.review); assertEquals(0,executions)
    }
    @Test fun `prepare never executes until its exact review is confirmed`() = runTest {
        signIn(); advanceUntilIdle(); model.loadControl(); advanceUntilIdle()
        model.prepare(TailscaleAction.Connect); advanceUntilIdle()
        val review = requireNotNull(model.review)
        assertEquals(0,executions); assertEquals(1,model.state.records.size)
        model.submit(review); advanceUntilIdle()
        assertEquals(1,executions); assertNull(model.state.control); assertNull(model.review)
        assertEquals(TailscaleOperationState.Applied,model.state.operation?.state)
        model.submit(review); advanceUntilIdle(); assertEquals(1,executions)
    }
    @Test fun `dismissed review cannot submit and backgrounding withdraws review`() = runTest {
        signIn(); advanceUntilIdle(); model.loadControl(); advanceUntilIdle(); model.prepare(TailscaleAction.Connect); advanceUntilIdle()
        val review = requireNotNull(model.review); model.dismiss(); model.submit(review); advanceUntilIdle(); assertEquals(0,executions)
        model.prepare(TailscaleAction.Connect); advanceUntilIdle(); val next = requireNotNull(model.review)
        model.setActive(false); model.submit(next); advanceUntilIdle(); assertNull(model.review); assertEquals(0,executions)
    }
    @Test fun `host account switch invalidates old confirmation`() = runTest {
        signIn(); advanceUntilIdle(); model.loadControl(); advanceUntilIdle(); model.prepare(TailscaleAction.Connect); advanceUntilIdle()
        val review = requireNotNull(model.review)
        signIn("bob"); advanceUntilIdle(); model.submit(review); advanceUntilIdle()
        assertEquals(0,executions); assertNull(model.review); assertNull(model.state.control)
    }
    @Test fun `background and resume cannot revive a pending preparation review`() = runTest {
        signIn(); advanceUntilIdle(); model.loadControl(); advanceUntilIdle()
        prepareHold = CompletableDeferred(); model.prepare(TailscaleAction.Connect); runCurrent()
        model.setActive(false); model.setActive(true); prepareHold!!.complete(Unit); advanceUntilIdle()
        assertNull(model.review); assertFalse(model.state.busy); assertEquals(0,executions)
        assertNotNull(model.state.operation)
        prepareHold = null; model.prepare(TailscaleAction.Connect); advanceUntilIdle()
        assertNotNull(model.review); assertEquals(0,executions)
    }
    @Test fun `lost response blocks changes until original receipt is read`() = runTest {
        signIn(); advanceUntilIdle(); model.loadControl(); advanceUntilIdle(); model.prepare(TailscaleAction.Connect); advanceUntilIdle()
        loseResponse = true; model.submit(requireNotNull(model.review)); advanceUntilIdle()
        assertEquals(TailscaleReferencePhase.Executing,model.state.records.single().phase)
        model.loadControl(); advanceUntilIdle(); assertFalse(model.editable)
        model.read(); advanceUntilIdle(); assertEquals(TailscaleOperationState.Applied,model.state.operation?.state)
        assertEquals(1,executions); assertTrue(model.state.records.all { it.phase == TailscaleReferencePhase.Known })
    }
    @Test fun `leaving during prepare cannot show a late confirmation`() = runTest {
        signIn(); advanceUntilIdle(); model.loadControl(); advanceUntilIdle()
        prepareHold = CompletableDeferred(); model.prepare(TailscaleAction.Connect); runCurrent()
        model.stop(); prepareHold!!.complete(Unit); advanceUntilIdle()
        assertNull(model.review); assertFalse(model.state.busy); assertEquals(0,executions)
    }
    @Test fun `unchanged configuration creates no executable review`() = runTest {
        signIn(); advanceUntilIdle(); model.loadControl(); advanceUntilIdle()
        model.prepare(TailscaleAction.Configure,baseline.configuration); advanceUntilIdle()
        assertNull(model.review); assertEquals(0,executions)
    }
    @Test fun `corrupt references remove controls and report storage failure`() = runTest {
        signIn(); advanceUntilIdle(); storage.bytes = byteArrayOf(1,2,3)
        model.loadControl(); advanceUntilIdle()
        assertTrue(model.state.storageFailed); assertNull(model.state.control); assertFalse(model.editable)
    }
}
