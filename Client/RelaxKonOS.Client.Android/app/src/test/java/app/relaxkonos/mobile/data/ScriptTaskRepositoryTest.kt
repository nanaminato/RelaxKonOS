package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class ScriptTaskRepositoryTest {
    private val base = FakeGateway()
    private var read: suspend () -> ApiResult<ScriptTasksResult> = { throw IllegalStateException("private detail") }
    private var sends = 0
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun scriptTasks(serverUrl: String, accessToken: String) = read()
        override suspend fun scriptSubmit(serverUrl: String, accessToken: String, request: ScriptRequest, key: String): ApiResult<ScriptTaskResult> {
            sends++
            throw IllegalStateException("private detail")
        }
        override suspend fun scriptCancel(serverUrl: String, accessToken: String, id: String): ApiResult<ScriptTaskResult> {
            sends++
            throw IllegalStateException("private detail")
        }
    }
    private val session = AuthSession(gateway)
    private val repository = ScriptTaskRepository(gateway, session, OperationIndex(object : OperationIndexStorage {
        override fun read(): ByteArray? = null
        override fun write(bytes: ByteArray) = Unit
    }))
    private suspend fun login(): SessionState.Active {
        base.onLogin = { _, _, _ -> ApiResult.Success(loginSession(userName = "alice")) }
        session.login(ServerConnectionIdentityRules.direct("https://host"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }

    @Test fun `unexpected read errors are safe failures and cancellation propagates`() = runTest {
        val owner = login()
        assertEquals(ApiResult.Transport(null), repository.tasks(owner))
        read = { throw CancellationException("cancelled") }
        assertTrue(runCatching { repository.tasks(owner) }.exceptionOrNull() is CancellationException)
    }

    @Test fun `unknown submit and cancel never replay writes`() = runTest {
        val owner = login()
        val request = ScriptRequest("/bin/true", emptyList(), "/tmp", emptyMap(), 30, "alice", null)
        assertEquals(ApiResult.Transport(null), repository.submit(owner, request, "request-key"))
        assertEquals(1, sends)
        assertEquals(ApiResult.Transport(null), repository.cancel(owner, "task-id"))
        assertEquals(2, sends)
    }

    @Test fun `session change during failed read cannot return into old owner`() = runTest {
        val owner = login()
        read = { login(); throw IllegalStateException("late failure") }
        assertTrue(runCatching { repository.tasks(owner) }.exceptionOrNull() is CancellationException)
    }
}
