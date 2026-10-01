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

class TerminalSettingsRepositoryTest {
    private val gateway = FakeGateway(); private val session = AuthSession(gateway)
    private val repository = TerminalSettingsRepository(gateway, session)
    private var actual = TerminalSettings.Default; private var sends = 0
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://host"), "alice", "pw".toCharArray()) {}
        gateway.onTerminalSettings = { id -> assertEquals("11111111-1111-1111-1111-111111111111", id); ApiResult.Success(actual) }
        gateway.onSaveTerminalSettings = { _, value -> sends++; actual = value; ApiResult.Success(value) }
        return session.state.value as SessionState.Active
    }
    @Test fun `saving current workspace preferences reads back all six fields`() = runTest {
        val owner = login(); val next = TerminalSettingsWire.scheme(actual.copy(fontSize = 20.0), "Light")
        assertEquals(next, (repository.save(owner, actual, next) as ApiResult.Success).value); assertEquals(1, sends)
    }
    @Test fun `settings changes before save refuse the write and never overwrite silently`() = runTest {
        val owner = login(); val previous = actual; actual = actual.copy(fontSize = 30.0)
        assertEquals(409, (repository.save(owner, previous, previous.copy(fontSize = 20.0)) as ApiResult.Problem).status)
        assertEquals(0, sends)
    }
    @Test fun `unknown transport and unreadable successful receipt never replay or report saved`() = runTest {
        val owner = login(); gateway.onSaveTerminalSettings = { _, _ -> sends++; ApiResult.Transport("timeout") }
        assertTrue(repository.save(owner, actual, actual.copy(fontSize = 20.0)) is ApiResult.Transport); assertEquals(1, sends)
        gateway.onSaveTerminalSettings = { _, value -> sends++; gateway.onTerminalSettings = { ApiResult.Transport(null) }; ApiResult.Success(value) }
        assertTrue(repository.save(owner, actual, actual.copy(fontSize = 20.0)) is ApiResult.Transport); assertEquals(2, sends)
    }
    @Test fun `changed owner rejects both stale reads and sends`() = runTest {
        val owner = login(); gateway.onTerminalSettings = { login(); ApiResult.Success(actual) }
        assertTrue(runCatching { repository.read(owner) }.exceptionOrNull() is CancellationException)
        assertTrue(runCatching { repository.save(owner, actual, actual) }.exceptionOrNull() is CancellationException); assertEquals(0, sends)
    }

    @Test fun `expired authentication rechecks workspace preferences before retrying the PUT`() = runTest {
        val owner = login(); val expected = actual
        gateway.onRefresh = { _, _ -> actual = actual.copy(fontSize = 30.0); ApiResult.Success(AuthTokens("new", "refresh-new", null, null)) }
        gateway.onSaveTerminalSettings = { _, _ -> sends++; ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null) }
        val result = repository.save(owner, expected, expected.copy(fontSize = 20.0))
        assertEquals(409, (result as ApiResult.Problem).status)
        assertEquals(1, sends)
    }
}
