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
import java.time.Instant
import java.util.UUID

class TailscaleLoginTest {
    private val base = FakeGateway()
    private val session = AuthSession(base)
    private val storage = TailscaleControlTest.Storage()
    private val references = TailscaleOperationReferences(storage)
    private val observation = TailscaleLoginObservation("B".repeat(64),"C".repeat(64),"Running",false)
    private val operations = mutableMapOf<String,TailscaleLoginOperation>()
    private val originalSessions = mutableMapOf<String,SessionState.Active>()
    private var state = TailscaleLoginState.Issued
    private var executes = 0
    private var closes = 0
    private var challenges = 0
    private var loseResponse = false
    private var badPlan = false
    private var badChallenge = false
    private var expiredChallenge = false
    private var instant: Instant? = null
    private var challengeHook: (suspend () -> Unit)? = null
    private var executeHook: (suspend () -> Unit)? = null
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun prepareTailscaleLogin(serverUrl: String, accessToken: String, id: String): ApiResult<TailscaleLoginOperation> {
            val plan = TailscaleLoginOperation(id,TailscaleLoginState.Prepared,"A".repeat(64),Instant.now().plusSeconds(240),Instant.now(),null,null)
            operations[id] = plan
            originalSessions[id] = session.state.value as SessionState.Active
            return ApiResult.Success(if (badPlan) plan.copy(operationId = UUID.randomUUID().toString()) else plan)
        }
        override suspend fun executeTailscaleLogin(serverUrl: String, accessToken: String, id: String): ApiResult<TailscaleLoginOperation> {
            executes++; val result = operations.getValue(id).copy(state = state); operations[id] = result
            executeHook?.invoke()
            return if (loseResponse) ApiResult.Transport(null) else ApiResult.Success(result)
        }
        override suspend fun tailscaleLoginOperation(serverUrl: String, accessToken: String, id: String) = ApiResult.Success(operations.getValue(id))
        override suspend fun tailscaleLoginChallenge(serverUrl: String, accessToken: String, id: String): ApiResult<TailscaleLoginChallenge> {
            challenges++
            if (session.state.value !== originalSessions[id]) return ApiResult.Problem(404,"tailscale.operation.not_found",null)
            challengeHook?.invoke()
            return ApiResult.Success(TailscaleLoginChallenge(if (badChallenge) UUID.randomUUID().toString() else id,URL,
                if (expiredChallenge) Instant.now().minusSeconds(1) else operations.getValue(id).expiresAt))
        }
        override suspend fun tailscaleLoginRecovery(serverUrl: String, accessToken: String, id: String) =
            ApiResult.Success(TailscaleLoginRecovery(id,observation,true,Instant.now().plusSeconds(240),null,null))
        override suspend fun closeTailscaleLogin(serverUrl: String, accessToken: String, id: String, revision: String): ApiResult<TailscaleLoginOperation> {
            closes++; val original = operations.getValue(id)
            val identity = TailscaleLoginIdentity(id,UUID.randomUUID().toString(),UUID.randomUUID().toString(),UUID.randomUUID().toString(),original.expectedRevision,original.expiresAt)
            val result = original.copy(closure = TailscaleLoginClosure(identity,observation,Instant.now())); operations[id] = result
            return if (loseResponse) ApiResult.Transport(null) else ApiResult.Success(result)
        }
    }
    private val elevations = ElevationRepository(gateway,session,CredentialVault(InMemoryVaultStorage(),FakeVaultCrypto()),UsageMemoryStore(InMemoryUsageMemoryStorage()))
    private val repository = TailscaleLoginRepository(gateway,session,elevations,references) { instant ?: Instant.now() }
    private suspend fun login(account: String = "alice",manage: Boolean = true): SessionState.Active {
        base.onLogin = { _,_,_ -> ApiResult.Success(loginSession().let { it.copy(userName = account,server = it.server.copy(capabilities = setOf(ServerCapabilities.TAILSCALE),privilegedOperations = manage)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"),account,"pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private suspend fun plan(owner: SessionState.Active) = (repository.prepare(owner,ElevationAnswerProvider.Declines) as ApiResult.Success).value
    private suspend fun issued(owner: SessionState.Active): TailscaleLoginOperation {
        val plan = plan(owner)
        return (repository.execute(owner,plan,true,ElevationAnswerProvider.Declines) as ApiResult.Success).value
    }
    private suspend fun reject(action: suspend () -> Unit) { assertTrue(runCatching { action() }.isFailure) }
    @Test fun `unconfirmed and expired plans cannot issue a login`() = runTest {
        val owner = login(); val plan = plan(owner)
        reject { repository.execute(owner,plan,false,ElevationAnswerProvider.Declines) }
        instant = plan.expiresAt.plusSeconds(1)
        reject { repository.execute(owner,plan,true,ElevationAnswerProvider.Declines) }; assertEquals(0,executes)
    }
    @Test fun `references must be saved before native submission`() = runTest {
        val owner = login(); val plan = plan(owner); storage.failWrites = true
        reject { repository.execute(owner,plan,true,ElevationAnswerProvider.Declines) }; assertEquals(0,executes)
    }
    @Test fun `bad prepare identity cannot enter local history`() = runTest {
        val owner = login(); badPlan = true
        reject { plan(owner) }; assertTrue(repository.records(owner).isEmpty())
    }
    @Test fun `lost response is read through original operation and never replayed`() = runTest {
        val owner = login(); val plan = plan(owner); loseResponse = true
        repository.execute(owner,plan,true,ElevationAnswerProvider.Declines)
        assertEquals(TailscaleReferencePhase.Executing,repository.records(owner).single().phase)
        reject { repository.execute(owner,plan,true,ElevationAnswerProvider.Declines) }
        assertEquals(TailscaleLoginState.Issued,(repository.read(owner,plan.operationId) as ApiResult.Success).value.state)
        assertEquals(1,executes)
    }
    @Test fun `same account new session can recover old operation but cannot borrow its challenge`() = runTest {
        val owner = login(); val operation = issued(owner)
        val next = login()
        assertEquals(1,repository.records(next).size)
        assertEquals(TailscaleLoginState.Issued,(repository.read(next,operation.operationId) as ApiResult.Success).value.state)
        var opened = 0
        assertTrue(repository.handoff(next,operation,ElevationAnswerProvider.Declines) { opened++; true } is ApiResult.Problem)
        assertEquals(0,opened)
        val preview = (repository.recovery(next,operation,ElevationAnswerProvider.Declines) as ApiResult.Success).value
        assertNotNull((repository.close(next,preview,true,ElevationAnswerProvider.Declines) as ApiResult.Success).value.closure)
    }
    @Test fun `valid handoff delivers only transient official URL and never persists it`() = runTest {
        val owner = login(); val operation = issued(owner); var opened = 0
        val result = repository.handoff(owner,operation,ElevationAnswerProvider.Declines) { url -> assertEquals(URL,url); opened++; true }
        assertEquals(true,(result as ApiResult.Success).value); assertEquals(1,opened)
        assertFalse(storage.bytes!!.decodeToString().contains("login.tailscale.com"))
        assertFalse(TailscaleLoginChallenge(operation.operationId,URL,operation.expiresAt).toString().contains(URL))
    }
    @Test fun `wrong challenge ID and expired challenge are never opened`() = runTest {
        val owner = login(); val operation = issued(owner); var opened = 0
        badChallenge = true
        reject { repository.handoff(owner,operation,ElevationAnswerProvider.Declines) { opened++; true } }
        badChallenge = false; expiredChallenge = true
        reject { repository.handoff(owner,operation,ElevationAnswerProvider.Declines) { opened++; true } }
        assertEquals(0,opened)
    }
    @Test fun `account switch while reading challenge rejects browser handoff`() = runTest {
        val owner = login(); val operation = issued(owner); var opened = 0
        challengeHook = { login("bob") }
        reject { repository.handoff(owner,operation,ElevationAnswerProvider.Declines) { opened++; true } }
        assertEquals(0,opened)
    }
    @Test fun `plan expiring during challenge read is not delivered`() = runTest {
        val owner = login(); val operation = issued(owner); var opened = 0
        challengeHook = { instant = operation.expiresAt.plusSeconds(1) }
        reject { repository.handoff(owner,operation,ElevationAnswerProvider.Declines) { opened++; true } }
        assertEquals(0,opened)
    }
    @Test fun `cancelled challenge read cannot launch from a late response`() = runTest {
        val owner = login(); val operation = issued(owner); var opened = 0
        lateinit var continuation: kotlin.coroutines.Continuation<Unit>
        challengeHook = { kotlin.coroutines.suspendCoroutine { continuation = it } }
        val waiting = launch { repository.handoff(owner,operation,ElevationAnswerProvider.Declines) { opened++; true } }
        yield(); waiting.cancel(); continuation.resumeWith(Result.success(Unit)); waiting.join()
        assertEquals(0,opened)
    }
    @Test fun `cancelled submission keeps its durable unresolved marker`() = runTest {
        val owner = login(); val plan = plan(owner)
        lateinit var continuation: kotlin.coroutines.Continuation<Unit>
        executeHook = { kotlin.coroutines.suspendCoroutine { continuation = it } }
        val waiting = launch { repository.execute(owner,plan,true,ElevationAnswerProvider.Declines) }
        yield(); waiting.cancel(); continuation.resumeWith(Result.success(Unit)); waiting.join()
        assertEquals(TailscaleReferencePhase.Executing,repository.records(owner).single().phase)
    }
    @Test fun `approval required operation can close without becoming completed`() = runTest {
        val owner = login(); state = TailscaleLoginState.ApprovalRequired
        val operation = issued(owner)
        val preview = (repository.recovery(owner,operation,ElevationAnswerProvider.Declines) as ApiResult.Success).value
        val result = repository.close(owner,preview,true,ElevationAnswerProvider.Declines) as ApiResult.Success
        assertEquals(TailscaleLoginState.ApprovalRequired,result.value.state); assertNotNull(result.value.closure)
        assertEquals(TailscaleReferencePhase.Known,repository.records(owner).single().phase)
    }
    @Test fun `closure response loss recovers by GET without reissuing closure`() = runTest {
        val owner = login(); val operation = issued(owner)
        val preview = (repository.recovery(owner,operation,ElevationAnswerProvider.Declines) as ApiResult.Success).value
        loseResponse = true; repository.close(owner,preview,true,ElevationAnswerProvider.Declines)
        assertEquals(TailscaleReferencePhase.Closing,repository.records(owner).single().phase)
        repository.read(owner,operation.operationId)
        assertEquals(TailscaleReferencePhase.Known,repository.records(owner).single().phase); assertEquals(1,closes)
    }
    @Test fun `completed receipt has no challenge or recovery entry`() = runTest {
        val owner = login(); state = TailscaleLoginState.Completed; val operation = issued(owner)
        reject { repository.handoff(owner,operation,ElevationAnswerProvider.Declines) { true } }
        reject { repository.recovery(owner,operation,ElevationAnswerProvider.Declines) }
        assertEquals(TailscaleReferencePhase.Known,repository.records(owner).single().phase)
    }
    @Test fun `read only account cannot begin login`() = runTest {
        val owner = login(manage = false); reject { plan(owner) }; assertEquals(0,executes)
    }
    companion object { const val URL = "https://login.tailscale.com/a/0123456789abcdef" }
}
