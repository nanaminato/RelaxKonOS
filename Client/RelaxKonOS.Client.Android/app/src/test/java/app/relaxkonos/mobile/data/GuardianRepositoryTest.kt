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

class GuardianRepositoryTest {
    private val gateway = FakeGateway(); private val session = AuthSession(gateway)
    private val repository = GuardianRepository(gateway, session)
    private val original = GuardianDefinition("job", "Job", "/bin/job", listOf("", " spaced ", "a\nb"), "/work", true,
        49, 17, GuardianHealthCheck("http", "https://host/health", 37, 11, 9), "alice", "uid:1042")
    private var actual: GuardianDefinition? = original; private var sends = 0
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession(userName = "alice")) }
        session.login(ServerConnectionIdentityRules.direct("https://host"), "alice", "pw".toCharArray()) {}
        gateway.onGuardianDefinition = { ApiResult.Success(GuardianDefinitionResult(actual != null, if (actual == null) "guardian.workload_not_found" else "", actual)) }
        gateway.onGuardianSave = { value, _ -> sends++; actual = value.copy(runAsIdentity = "uid:1042"); ApiResult.Success(GuardianDefinitionResult(true, "", actual)) }
        return session.state.value as SessionState.Active
    }
    @Test fun `successful edit reads back the complete definition and clears approval`() = runTest {
        val owner = login(); val next = original.copy(name = "Renamed")
        val password = "secret".toCharArray()
        assertEquals(next, (repository.save(owner, original, next, GuardianApproval("root", password)) as ApiResult.Success).value)
        assertEquals(1, sends); assertTrue(password.all { it == '\u0000' })
    }
    @Test fun `creating refuses an existing ID and edited definitions refuse stale baselines`() = runTest {
        val owner = login()
        assertEquals(409, (repository.save(owner, null, original, null) as ApiResult.Problem).status)
        actual = original.copy(maxRestartAttempts = 90)
        assertEquals(409, (repository.save(owner, original, original.copy(name = "Changed"), null) as ApiResult.Problem).status)
        assertEquals(0, sends)
    }
    @Test fun `new workload accepts authoritative executable resolution and stable identity`() = runTest {
        val owner = login(); actual = null
        val request = original.copy(executablePath = "job", runAsIdentity = null)
        gateway.onGuardianSave = { value, _ -> sends++; actual = value.copy(executablePath = "/usr/bin/job", runAsIdentity = "uid:1042"); ApiResult.Success(GuardianDefinitionResult(true, "", actual)) }
        val result = repository.save(owner, null, request, null)
        assertEquals(actual, (result as ApiResult.Success).value)
        assertEquals(1, sends)
    }
    @Test fun `unknown save and mismatched readback never replay or claim success`() = runTest {
        val owner = login()
        gateway.onGuardianSave = { _, _ -> sends++; ApiResult.Transport("timeout") }
        assertTrue(repository.save(owner, original, original, null) is ApiResult.Transport); assertEquals(1, sends)
        gateway.onGuardianSave = { value, _ -> sends++; actual = value.copy(stopTimeoutSeconds = 15); ApiResult.Success(GuardianDefinitionResult(true, "", value)) }
        assertTrue(repository.save(owner, original, original, null) is ApiResult.Transport); assertEquals(2, sends)
    }
    @Test fun `canonical host account in this save receipt is verified rather than guessed from a later read`() = runTest {
        val owner = login()
        gateway.onGuardianSave = { value, _ -> sends++; actual = value.copy(runAs = "HOST\\alice", runAsIdentity = "S-1-5-21-1042"); ApiResult.Success(GuardianDefinitionResult(true, "", actual)) }
        val result = repository.save(owner, original, original, null)
        assertEquals("HOST\\alice", (result as ApiResult.Success).value.runAs)
        assertEquals("S-1-5-21-1042", result.value.runAsIdentity); assertEquals(1, sends)
    }
    @Test fun `login switch during preflight cancels before sending and clears approval`() = runTest {
        val owner = login(); val password = "secret".toCharArray()
        gateway.onGuardianDefinition = { login(); ApiResult.Success(GuardianDefinitionResult(true, "", original)) }
        assertTrue(runCatching { repository.save(owner, original, original, GuardianApproval("root", password)) }.exceptionOrNull() is CancellationException)
        assertEquals(0, sends); assertTrue(password.all { it == '\u0000' })
    }
    @Test fun `refresh repeats preflight and refuses a definition changed during authentication`() = runTest {
        val owner = login()
        gateway.onRefresh = { _, _ -> actual = original.copy(name = "Other editor"); ApiResult.Success(AuthTokens("new", "refresh-new", null, null)) }
        gateway.onGuardianSave = { _, _ -> sends++; ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null) }
        assertEquals(409, (repository.save(owner, original, original, null) as ApiResult.Problem).status); assertEquals(1, sends)
    }
    @Test fun `missing definitions agent failures and target mismatch remain distinct`() = runTest {
        val owner = login(); actual = null
        assertEquals(404, (repository.definition(owner, "job") as ApiResult.Problem).status)
        gateway.onGuardianDefinition = { ApiResult.Success(GuardianDefinitionResult(false, "guardian.agent_timeout", null)) }
        assertEquals(503, (repository.definition(owner, "job") as ApiResult.Problem).status)
        gateway.onGuardianDefinition = { ApiResult.Success(GuardianDefinitionResult(true, "", original.copy(id = "other"))) }
        assertTrue(repository.definition(owner, "job") is ApiResult.Transport)
    }
    @Test fun `closed action set and unsuccessful receipts cannot look successful`() = runTest {
        val owner = login()
        gateway.onGuardianAction = { id, action -> assertEquals("job", id); assertEquals("start", action); sends++; ApiResult.Success(GuardianOperation(false, "guardian.run_as_identity_mismatch")) }
        assertEquals("guardian.run_as_identity_mismatch", (repository.action(owner, "job", "start") as ApiResult.Problem).code)
        assertTrue(runCatching { repository.action(owner, "job", "arbitrary") }.isFailure); assertEquals(1, sends)
    }
}
