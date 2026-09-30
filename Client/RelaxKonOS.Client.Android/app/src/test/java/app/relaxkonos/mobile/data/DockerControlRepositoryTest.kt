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

class DockerControlRepositoryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway(); private val session = AuthSession(gateway); private val storage = Storage()
    private val journal = DockerControlJournal(storage); private val repository = DockerControlRepository(gateway, session, journal, testDockerGate(journal))
    private val id = "11111111-1111-1111-1111-111111111111"
    private val status = DockerStatus(true, "", "28", "linux", "amd64")
    private val mirror = DockerImageMirror(id, "Mirror", "mirror.example", false)
    private val facts = DockerControlFacts(status, listOf(DockerImageMirror(DockerImageMirror.DEFAULT_ID, "Default", "", true), mirror))
    private suspend fun signIn(manage: Boolean = true, account: String = "alice"): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(userName = account,
            server = it.server.copy(capabilities = setOf(ServerCapabilities.DOCKER), privilegedOperations = manage)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), account, "pw".toCharArray()) {}
        gateway.onDockerStatus = { ApiResult.Success(status) }; gateway.onDockerMirrors = { ApiResult.Success(facts.mirrors) }
        return session.state.value as SessionState.Active
    }
    @Test fun `lost mirror creation persists no endpoint body and is never replayed`() = runTest {
        val owner = signIn(); var calls = 0
        val change = DockerControlChange(DockerControlKind.MirrorCreate, mirror = DockerMirrorRequest("PrivateName", "private.example"))
        gateway.onDockerCreateMirror = { calls++; ApiResult.Transport(null) }
        assertTrue(repository.change(owner, facts, change) is ApiResult.Transport)
        assertFalse(storage.bytes!!.decodeToString().contains("private.example")); assertFalse(storage.bytes!!.decodeToString().contains("PrivateName"))
        repository.facts(owner); val pending = DockerControlJournal(storage).pending(owner).single()
        assertTrue(repository.change(owner, facts, change) is ApiResult.Problem); assertEquals(1, calls)
        assertTrue(repository.acceptFacts(owner, pending) is ApiResult.Success); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `engine stop succeeds with unavailable post action status after explicit confirmation`() = runTest {
        val owner = signIn()
        gateway.onDockerEngineAction = { action, confirmed ->
            assertEquals(DockerEngineAction.Stop, action); assertTrue(confirmed)
            val stopped = status.copy(available = false, problemCode = "docker.daemon_unavailable")
            gateway.onDockerStatus = { ApiResult.Success(stopped) }; ApiResult.Success(DockerEngineResult(true, "", stopped))
        }
        assertTrue(repository.change(owner, facts, DockerControlChange(DockerControlKind.EngineStop)) is ApiResult.Success)
        assertTrue(repository.pending(owner).isEmpty()); assertFalse((repository.facts(owner) as ApiResult.Success).value.status.available)
    }
    @Test fun `state or mirror change refuses write before starting local marker`() = runTest {
        val owner = signIn(); gateway.onDockerMirrors = { ApiResult.Success(facts.mirrors.map { it.copy(selected = !it.selected) }) }
        val result = repository.change(owner, facts, DockerControlChange(DockerControlKind.EngineRestart))
        assertEquals("docker.control.facts_changed", (result as ApiResult.Problem).code); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `failed or unverified post action retains marker and failed reads cannot adopt`() = runTest {
        val owner = signIn(); gateway.onDockerEngineAction = { _, _ -> ApiResult.Success(DockerEngineResult(false, "docker.engine.problem.action_failed", status)) }
        assertTrue(repository.change(owner, facts, DockerControlChange(DockerControlKind.EngineRestart)) is ApiResult.Problem)
        val pending = repository.pending(owner).single(); gateway.onDockerMirrors = { ApiResult.Transport(null) }
        assertTrue(repository.acceptFacts(owner, pending) is ApiResult.Transport); assertEquals(1, repository.pending(owner).size)
    }
    @Test fun `selected default becomes null and protected default cannot be edited or deleted`() = runTest {
        val owner = signIn(); gateway.onDockerSelectMirror = { assertNull(it); ApiResult.Success(Unit) }
        assertTrue(repository.change(owner, facts, DockerControlChange(DockerControlKind.MirrorSelect, DockerImageMirror.DEFAULT_ID)) is ApiResult.Success)
        listOf(DockerControlKind.MirrorDelete, DockerControlKind.MirrorUpdate).forEach {
            assertTrue(runCatching { repository.change(owner, facts, DockerControlChange(it, DockerImageMirror.DEFAULT_ID, DockerMirrorRequest("Default", "host"))) }.isFailure)
        }
        assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `observer switched identity and damaged journal cannot submit or erase pending changes`() = runTest {
        val observer = signIn(false)
        assertTrue(runCatching { repository.change(observer, facts, DockerControlChange(DockerControlKind.EngineStart)) }.isFailure)
        val owner = signIn(); storage.bytes = byteArrayOf(1, 2)
        assertTrue(runCatching { repository.change(owner, facts, DockerControlChange(DockerControlKind.EngineStart)) }.isFailure)
        storage.bytes = null; signIn(account = "bob")
        assertTrue(runCatching { repository.facts(owner) }.exceptionOrNull() is CancellationException)
    }
    @Test fun `wrong update response cannot verify another mirror write`() = runTest {
        val owner = signIn(); gateway.onDockerUpdateMirror = { _, _ -> ApiResult.Success(mirror.copy(id = "22222222-2222-2222-2222-222222222222")) }
        assertTrue(repository.change(owner, facts, DockerControlChange(DockerControlKind.MirrorUpdate, id, DockerMirrorRequest("Changed", "host"))) is ApiResult.Transport)
        assertEquals(1, repository.pending(owner).size)
    }
    @Test fun `authentication retry rereads facts and refuses changed host before repeating rejected write`() = runTest {
        val owner = signIn(); var calls = 0
        gateway.onDockerEngineAction = { _, _ -> calls++; ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null) }
        gateway.onRefresh = { _, _ ->
            gateway.onDockerStatus = { ApiResult.Success(status.copy(available = false, problemCode = "docker.daemon_unavailable")) }
            ApiResult.Success(AuthTokens("fresh", "refresh-2", null, null))
        }
        assertEquals("docker.control.facts_changed", (repository.change(owner, facts, DockerControlChange(DockerControlKind.EngineRestart)) as ApiResult.Problem).code)
        assertEquals(1, calls); assertEquals(1, repository.pending(owner).size)
    }
    @Test fun `authentication retry uses same approved change and only resumes rejected request`() = runTest {
        val owner = signIn(); var calls = 0
        gateway.onDockerEngineAction = { action, confirmed ->
            assertEquals(DockerEngineAction.Restart, action); assertTrue(confirmed); calls++
            if (calls == 1) ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null) else ApiResult.Success(DockerEngineResult(true, "", status))
        }
        gateway.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("fresh", "refresh-2", null, null)) }
        assertTrue(repository.change(owner, facts, DockerControlChange(DockerControlKind.EngineRestart)) is ApiResult.Success)
        assertEquals(2, calls); assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `success followed by lost read remains uncertain`() = runTest {
        val owner = signIn(); gateway.onDockerDeleteMirror = { gateway.onDockerStatus = { ApiResult.Transport(null) }; ApiResult.Success(Unit) }
        assertTrue(repository.change(owner, facts, DockerControlChange(DockerControlKind.MirrorDelete, id)) is ApiResult.Success)
        assertEquals(1, repository.pending(owner).size)
    }
}
