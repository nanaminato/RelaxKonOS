package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import app.relaxkonos.mobile.security.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class FileBatchRunnerTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val elevations = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()))
    private val files = FilesRepository(gateway, session, elevations)
    private val runner = FileBatchRunner(files, session)
    private val entries = listOf("a", "b", "c").map { RemoteEntry("/src/$it", it, false, 10, 0, null) }
    private val noElevation = ElevationAnswerProvider { _, _ -> null }
    private suspend fun signIn(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `partial rejection continues and only confirmed successes are completed`() = runTest {
        val owner = signIn(); val sends = mutableListOf<String>()
        gateway.onCopy = { _, _, path, dest -> sends += path; assertEquals("/dest/" + path.substringAfterLast('/'), dest)
            if (path.endsWith('b')) ApiResult.Problem(409, "already-exists", null) else ApiResult.Success(Unit) }
        val result = runner.run(owner, entries, FileBatchAction.Copy, { "/dest/${it.name}" }, noElevation, { false }) { _, _ -> }
        assertEquals(3, sends.size); assertEquals(listOf("/src/a", "/src/c"), result.completed)
        assertEquals("/src/b", result.failures.single().path); assertFalse(result.failures.single().unknown)
        assertTrue(result.skipped.isEmpty())
    }
    @Test fun `unknown transport stops with no replay and untouched remainder`() = runTest {
        val owner = signIn(); var sends = 0
        gateway.onMove = { _, _, _, _ -> sends++; ApiResult.Transport("timeout") }
        val result = runner.run(owner, entries, FileBatchAction.Move, { "/dest/${it.name}" }, noElevation, { false }) { _, _ -> }
        assertEquals(1, sends); assertTrue(result.failures.single().unknown); assertEquals(listOf("/src/b", "/src/c"), result.skipped)
    }
    @Test fun `server failure is unknown and declined elevation stops after one prompt`() = runTest {
        val owner = signIn(); var sends = 0; var prompts = 0
        gateway.onDelete = { _, _, _ -> sends++; ApiResult.Problem(500, "io-error", null) }
        val failed = runner.run(owner, entries, FileBatchAction.Delete, { "" }, noElevation, { false }) { _, _ -> }
        assertTrue(failed.failures.single().unknown); assertEquals(1, sends)
        gateway.onDelete = { _, _, _ -> sends++; ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) }
        val declined = runner.run(owner, entries, FileBatchAction.Delete, { "" }, ElevationAnswerProvider { _, _ -> prompts++; null }, { false }) { _, _ -> }
        assertEquals(1, prompts); assertEquals(2, sends); assertFalse(declined.failures.single().unknown); assertEquals(2, declined.skipped.size)
    }
    @Test fun `stop waits for current item result and skips later items`() = runTest {
        val owner = signIn(); var stopped = false; var sends = 0
        gateway.onDelete = { _, _, _ -> sends++; stopped = true; ApiResult.Success(Unit) }
        val result = runner.run(owner, entries, FileBatchAction.Delete, { "" }, noElevation, { stopped }) { _, _ -> }
        assertEquals(1, sends); assertEquals(listOf("/src/a"), result.completed); assertEquals(2, result.skipped.size)
    }
    @Test fun `changed owner cannot dispatch or publish old file results`() = runTest {
        val owner = signIn(); var sends = 0
        gateway.onDelete = { _, _, _ -> sends++; signIn(); ApiResult.Success(Unit) }
        val late = runCatching { runner.run(owner, entries, FileBatchAction.Delete, { "" }, noElevation, { false }) { _, _ -> } }
        assertTrue(late.exceptionOrNull() is CancellationException); assertEquals(1, sends)
        val next = runCatching { runner.run(owner, entries, FileBatchAction.Delete, { "" }, noElevation, { false }) { _, _ -> } }
        assertTrue(next.exceptionOrNull() is CancellationException); assertEquals(1, sends)
    }
}
