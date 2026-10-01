package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class TextEditorRepositoryTest {
    private val base = FakeGateway()
    private val file = RemoteTextFile("/a", "hello", "a".repeat(64), "utf-8", false, "none")
    private var saves = 0
    private var onRead: suspend () -> ApiResult<RemoteTextFile> = { ApiResult.Success(file) }
    private var onSave: suspend () -> ApiResult<RemoteTextFile> = { ApiResult.Transport(null) }
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun textFile(serverUrl: String, accessToken: String, path: String) = onRead()
        override suspend fun saveTextFile(serverUrl: String, accessToken: String, file: RemoteTextFile, content: String): ApiResult<RemoteTextFile> { saves++; return onSave() }
    }
    private val session = AuthSession(gateway)
    private val client = TextEditorRepository(gateway, session)
    private suspend fun signIn(): SessionState.Active {
        base.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `transport loss sends one conditional write without replay`() = runTest {
        val owner = signIn()
        assertTrue(client.save(owner, file, "new", null) is ApiResult.Transport)
        assertEquals(1, saves)
    }
    @Test fun `wrong path and mismatched save receipt remain unknown`() = runTest {
        val owner = signIn(); onRead = { ApiResult.Success(file.copy(path = "/wrong")) }
        assertTrue(client.read(owner, "/a", null) is ApiResult.Transport)
        onSave = { ApiResult.Success(file.copy(content = "other")) }
        assertTrue(client.save(owner, file, "new", null) is ApiResult.Transport)
    }
    @Test fun `old session cannot read or save and late response is discarded`() = runTest {
        val owner = signIn(); val entered = CompletableDeferred<Unit>(); val release = CompletableDeferred<Unit>()
        onRead = { entered.complete(Unit); release.await(); ApiResult.Success(file) }
        val result = async { runCatching { client.read(owner, "/a", null) } }
        entered.await(); signIn(); release.complete(Unit)
        assertTrue(result.await().exceptionOrNull() is CancellationException)
        assertTrue(runCatching { client.save(owner, file, "new", null) }.exceptionOrNull() is CancellationException)
        assertEquals(0, saves)
    }
}
