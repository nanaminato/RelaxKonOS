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

class TailscaleControlTest {
    internal class Storage : TailscaleReferenceStorage {
        var bytes: ByteArray? = null
        var failWrites = false
        override fun read() = bytes
        override fun write(bytes: ByteArray) { check(!failWrites); this.bytes = bytes.copyOf() }
        override fun <T> locked(action: () -> T) = action()
    }
    private val base = FakeGateway()
    private val session = AuthSession(base)
    private val storage = Storage()
    private val references = TailscaleOperationReferences(storage)
    private val baseline = TailscaleControl("A".repeat(64),false,true,TailscaleConfiguration(true,false),true,null)
    private var executions = 0
    private var closes = 0
    private var loseResponse = false
    private var badPlan = false
    private var badResult = false
    private var needsAuthorization = false
    private var authorized = false
    private var instant: Instant? = null
    private var execution: (suspend () -> Unit)? = null
    private var closeHook: (suspend () -> Unit)? = null
    private val records = mutableMapOf<String,TailscaleOperation>()
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun tailscaleControl(serverUrl: String, accessToken: String): ApiResult<TailscaleControl> =
            if (needsAuthorization && !authorized) ApiResult.Problem(403,"tailscale.elevation_required",null) else ApiResult.Success(baseline)
        override suspend fun prepareTailscale(serverUrl: String, accessToken: String, request: TailscalePlanRequest): ApiResult<TailscaleOperation> {
            val plan = TailscaleOperation(request.operationId,request.action,TailscaleOperationState.Prepared,request.expectedRevision,Instant.now().plusSeconds(240),Instant.now(),request.configuration,null,null)
            records[plan.operationId] = plan
            return ApiResult.Success(if (badPlan) plan.copy(operationId = UUID.randomUUID().toString()) else plan)
        }
        override suspend fun executeTailscale(serverUrl: String, accessToken: String, id: String): ApiResult<TailscaleOperation> {
            if (needsAuthorization && !authorized) return ApiResult.Problem(403,"tailscale.elevation_required",null)
            executions++
            assertEquals(TailscaleReferencePhase.Executing,references.read(session.state.value as SessionState.Active).first { it.operationId == id }.phase)
            val result = records.getValue(id).copy(state = TailscaleOperationState.Applied)
            records[id] = result
            execution?.invoke()
            return if (loseResponse) ApiResult.Transport(null) else ApiResult.Success(if (badResult) result.copy(operationId = UUID.randomUUID().toString()) else result)
        }
        override suspend fun tailscaleOperation(serverUrl: String, accessToken: String, id: String) = ApiResult.Success(records.getValue(id))
        override suspend fun tailscaleRecovery(serverUrl: String, accessToken: String, id: String) = ApiResult.Success(TailscaleRecovery(id,baseline,true,Instant.now().plusSeconds(240),null,null))
        override suspend fun closeTailscale(serverUrl: String, accessToken: String, id: String, revision: String): ApiResult<TailscaleOperation> {
            closes++
            closeHook?.invoke()
            val result = records.getValue(id).copy(closure = TailscaleClosure(id,baseline,Instant.now(),UUID.randomUUID().toString()))
            records[id] = result
            return if (loseResponse) ApiResult.Transport(null) else ApiResult.Success(result)
        }
    }
    private val elevations = ElevationRepository(gateway,session,CredentialVault(InMemoryVaultStorage(),FakeVaultCrypto()),UsageMemoryStore(InMemoryUsageMemoryStorage()))
    private val repository = TailscaleControlRepository(gateway,session,elevations,references) { instant ?: Instant.now() }
    private suspend fun signIn(account: String = "alice", manage: Boolean = true): SessionState.Active {
        base.onLogin = { _,_,_ -> ApiResult.Success(loginSession().let { it.copy(userName = account,server = it.server.copy(capabilities = setOf(ServerCapabilities.TAILSCALE),privilegedOperations = manage)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"),account,"pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private suspend fun plan(owner: SessionState.Active) = (repository.prepare(owner,baseline,TailscaleAction.Connect,null,ElevationAnswerProvider.Declines) as ApiResult.Success).value
    private suspend fun reject(action: suspend () -> Unit) { assertTrue(runCatching { action() }.isFailure) }
    @Test fun `lost response is discoverable without replay and unrelated reads do not resolve it`() = runTest {
        val owner = signIn(); val first = plan(owner); val second = plan(owner)
        loseResponse = true
        assertTrue(repository.execute(owner,first,true,ElevationAnswerProvider.Declines) is ApiResult.Transport)
        repository.read(owner,second.operationId)
        assertEquals(TailscaleReferencePhase.Executing,references.read(owner).first { it.operationId == first.operationId }.phase)
        reject { repository.execute(owner,first,true,ElevationAnswerProvider.Declines) }
        repository.read(owner,first.operationId)
        assertEquals(1,executions)
        assertTrue(references.read(owner).all { it.phase == TailscaleReferencePhase.Known })
    }
    @Test fun `storage failure prevents native submission`() = runTest {
        val owner = signIn(); val plan = plan(owner); storage.failWrites = true
        reject { repository.execute(owner,plan,true,ElevationAnswerProvider.Declines) }
        assertEquals(0,executions)
    }
    @Test fun `prepared read cannot clear an uncertain submitted operation`() = runTest {
        val owner = signIn(); val plan = plan(owner); loseResponse = true
        repository.execute(owner,plan,true,ElevationAnswerProvider.Declines)
        val applied = records.getValue(plan.operationId); records[plan.operationId] = plan
        repository.read(owner,plan.operationId)
        assertEquals(TailscaleReferencePhase.Executing,references.read(owner).single().phase)
        reject { plan(owner) }; assertEquals(1,executions)
        records[plan.operationId] = applied; repository.read(owner,plan.operationId)
        assertEquals(TailscaleReferencePhase.Known,references.read(owner).single().phase)
    }
    @Test fun `failed prepare reference persistence cannot create executable plan`() = runTest {
        val owner = signIn(); storage.failWrites = true
        reject { plan(owner) }; assertEquals(0,executions); assertTrue(references.read(owner).isEmpty())
    }
    @Test fun `explicit confirmation and unexpired plan are required`() = runTest {
        val owner = signIn(); val plan = plan(owner)
        reject { repository.execute(owner,plan,false,ElevationAnswerProvider.Declines) }
        reject { repository.execute(owner,plan.copy(expiresAt = Instant.now().minusSeconds(1)),true,ElevationAnswerProvider.Declines) }
        assertEquals(0,executions)
    }
    @Test fun `wrong prepare identity never enters local references`() = runTest {
        val owner = signIn(); badPlan = true
        reject { plan(owner) }; assertTrue(references.read(owner).isEmpty())
    }
    @Test fun `wrong execution identity keeps unresolved marker`() = runTest {
        val owner = signIn(); val plan = plan(owner); badResult = true
        reject { repository.execute(owner,plan,true,ElevationAnswerProvider.Declines) }
        assertEquals(TailscaleReferencePhase.Executing,references.read(owner).single().phase)
    }
    @Test fun `account change after execute cannot accept old result`() = runTest {
        val owner = signIn(); val plan = plan(owner)
        execution = { signIn("bob") }
        reject { repository.execute(owner,plan,true,ElevationAnswerProvider.Declines) }
        assertEquals(TailscaleReferencePhase.Executing,references.read(owner).single().phase)
    }
    @Test fun `unknown receipt never clears pending submission without closure`() = runTest {
        val owner = signIn(); val plan = plan(owner); loseResponse = true
        repository.execute(owner,plan,true,ElevationAnswerProvider.Declines)
        records[plan.operationId] = plan.copy(state = TailscaleOperationState.Unknown)
        repository.read(owner,plan.operationId)
        assertEquals(TailscaleReferencePhase.Executing,references.read(owner).single().phase)
    }
    @Test fun `closure response loss recovers from original receipt without repeat close`() = runTest {
        val owner = signIn(); val plan = plan(owner)
        records[plan.operationId] = plan.copy(state = TailscaleOperationState.Unknown)
        val preview = (repository.recovery(owner,plan.operationId,ElevationAnswerProvider.Declines) as ApiResult.Success).value
        loseResponse = true
        repository.close(owner,preview,true,ElevationAnswerProvider.Declines)
        assertEquals(TailscaleReferencePhase.Closing,references.read(owner).single().phase)
        repository.read(owner,plan.operationId)
        assertEquals(TailscaleReferencePhase.Known,references.read(owner).single().phase); assertEquals(1,closes)
    }
    @Test fun `expired recovery and missing confirmation never close`() = runTest {
        val owner = signIn(); val plan = plan(owner)
        val preview = TailscaleRecovery(plan.operationId,baseline,true,Instant.now().minusSeconds(1),null,null)
        reject { repository.close(owner,preview,true,ElevationAnswerProvider.Declines) }
        reject { repository.close(owner,preview.copy(expiresAt = Instant.now().plusSeconds(120)),false,ElevationAnswerProvider.Declines) }
        assertEquals(0,closes)
    }
    @Test fun `unchanged configuration and read only account cannot prepare`() = runTest {
        val owner = signIn()
        reject { repository.prepare(owner,baseline,TailscaleAction.Configure,baseline.configuration,ElevationAnswerProvider.Declines) }
        val reader = signIn(manage = false)
        reject { plan(reader) }; assertTrue(references.read(owner).isEmpty())
    }
    @Test fun `references survive reconstruction and isolate host account workspace`() = runTest {
        val owner = signIn(); val plan = plan(owner)
        references.mark(owner,plan.operationId,TailscaleReferencePhase.Executing)
        assertEquals(TailscaleReferencePhase.Executing,TailscaleOperationReferences(storage).read(owner).single().phase)
        listOf(owner.copy(serviceId = "other"),owner.copy(userName = "other"),owner.copy(workspaceId = "other")).forEach { assertTrue(references.read(it).isEmpty()) }
        val text = storage.bytes!!.decodeToString()
        assertFalse(text.contains("acceptDns")); assertFalse(text.contains("Prepared")); assertFalse(text.contains("pw"))
    }
    @Test fun `retention never evicts unresolved reference`() = runTest {
        val owner = signIn(); val first = UUID.randomUUID().toString()
        references.remember(owner,first); references.mark(owner,first,TailscaleReferencePhase.Executing)
        repeat(40) { references.remember(owner,UUID.randomUUID().toString()) }
        assertEquals(30,references.read(owner).size); assertTrue(references.read(owner).any { it.operationId == first })
    }
    @Test fun `corrupt storage fails closed instead of losing unresolved identities`() = runTest {
        val owner = signIn(); storage.bytes = byteArrayOf(1,2,3)
        reject { repository.records(owner) }; reject { plan(owner) }; assertEquals(0,executions)
    }
    @Test fun `cancelled execute cannot accept a late successful response`() = runTest {
        val owner = signIn(); val plan = plan(owner)
        lateinit var resume: kotlin.coroutines.Continuation<Unit>
        execution = { kotlin.coroutines.suspendCoroutine { resume = it } }
        val pending = launch { repository.execute(owner,plan,true,ElevationAnswerProvider.Declines) }
        yield(); pending.cancel(); resume.resumeWith(Result.success(Unit)); pending.join()
        assertEquals(TailscaleReferencePhase.Executing,references.read(owner).single().phase)
    }
    @Test fun `original receipt read waits until submission finishes`() = runTest {
        val owner = signIn(); val plan = plan(owner); val release = CompletableDeferred<Unit>()
        execution = { release.await() }
        val write = launch { repository.execute(owner,plan,true,ElevationAnswerProvider.Declines) }; yield()
        val read = async { repository.read(owner,plan.operationId) }; yield()
        assertFalse(read.isCompleted); release.complete(Unit); write.join(); read.await()
        assertEquals(1,executions); assertEquals(TailscaleReferencePhase.Known,references.read(owner).single().phase)
    }
    @Test fun `real file publication survives reopen and exclusive lock rejects competing read`() = runTest {
        val owner = signIn(); val folder = java.nio.file.Files.createTempDirectory("tailscale-refs-").toFile()
        try {
            val first = TailscaleOperationReferences(FileTailscaleReferenceStorage(folder) {}); val id = UUID.randomUUID().toString()
            first.remember(owner,id); first.mark(owner,id,TailscaleReferencePhase.Executing)
            val second = TailscaleOperationReferences(FileTailscaleReferenceStorage(folder) {})
            assertEquals(TailscaleReferencePhase.Executing,second.read(owner).single().phase)
            java.io.RandomAccessFile(java.io.File(folder,"tailscale-operations.lock"),"rw").use { lock ->
                lock.channel.lock().use { assertTrue(runCatching { second.read(owner) }.isFailure) }
            }
        } finally { folder.deleteRecursively() }
    }
    @Test fun `domain authorization uses exact host network capability and resource once`() = runTest {
        val owner = signIn(); needsAuthorization = true; var prompts = 0
        base.onElevation = { _,_,capability,target,_,_ ->
            assertEquals("hostNetworkChange",capability); assertEquals("host/tailscale",target)
            authorized = true; ApiResult.Success(ElevationGrant(true,null))
        }
        val result = repository.control(owner,ElevationAnswerProvider { _,_ -> prompts++; ElevationAnswer("alice","admin".toCharArray()) })
        assertTrue(result is ApiResult.Success); assertEquals(1,prompts)
    }
    @Test fun `plan expiring during elevation cannot reach native submission`() = runTest {
        val owner = signIn(); val plan = plan(owner); needsAuthorization = true
        base.onElevation = { _,_,_,_,_,_ -> authorized = true; ApiResult.Success(ElevationGrant(true,null)) }
        val result = repository.execute(owner,plan,true,ElevationAnswerProvider { _,_ ->
            instant = plan.expiresAt.plusSeconds(1); ElevationAnswer("alice","admin".toCharArray())
        })
        assertEquals("tailscale.plan_expired",(result as ApiResult.Problem).code); assertEquals(0,executions)
        repository.read(owner,plan.operationId)
        assertEquals(TailscaleReferencePhase.Executing,references.read(owner).single().phase)
        records[plan.operationId] = plan.copy(state = TailscaleOperationState.Rejected)
        repository.read(owner,plan.operationId)
        assertEquals(TailscaleReferencePhase.Known,references.read(owner).single().phase)
    }
    @Test fun `directory sync failure before submission refuses native action`() = runTest {
        val owner = signIn(); val folder = java.nio.file.Files.createTempDirectory("tailscale-sync-").toFile()
        var fail = false
        try {
            val journal = TailscaleOperationReferences(FileTailscaleReferenceStorage(folder) { check(!fail) })
            val client = TailscaleControlRepository(gateway,session,elevations,journal)
            val plan = (client.prepare(owner,baseline,TailscaleAction.Connect,null,ElevationAnswerProvider.Declines) as ApiResult.Success).value
            fail = true
            reject { client.execute(owner,plan,true,ElevationAnswerProvider.Declines) }
            assertEquals(0,executions)
        } finally { folder.deleteRecursively() }
    }
    @Test fun `newly observed unknown result creates a durable unresolved marker`() = runTest {
        val owner = signIn(); val plan = plan(owner)
        records[plan.operationId] = plan.copy(state = TailscaleOperationState.Unknown)
        repository.read(owner,plan.operationId)
        assertEquals(TailscaleReferencePhase.Executing,references.read(owner).single().phase)
        reject { plan(owner) }; assertEquals(0,executions)
    }
}
