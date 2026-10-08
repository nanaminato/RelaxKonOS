package app.relaxkonos.mobile.ui.editor

import androidx.compose.ui.text.input.TextFieldValue
import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.TextEditorRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Before
import org.junit.After
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class TextEditorViewModelTest {
    private val base = FakeGateway()
    private val reads = mutableListOf<String>()
    private var creates = 0
    private val file = RemoteTextFile("/old", "remote", "a".repeat(64), "utf-8", false, "none")
    private var onRead: suspend (String) -> ApiResult<RemoteTextFile> = { ApiResult.Success(file.copy(path = it)) }
    private var onCreate: suspend () -> ApiResult<RemoteTextFile> = { ApiResult.Transport(null) }
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun textFile(serverUrl: String, accessToken: String, path: String): ApiResult<RemoteTextFile> {
            reads += path
            return onRead(path)
        }
        override suspend fun createTextFile(serverUrl: String, accessToken: String, path: String,
            content: String, encoding: String, bom: Boolean): ApiResult<RemoteTextFile> {
            creates++
            return onCreate()
        }
    }
    private val session = AuthSession(gateway)
    private val store = ViewModelStore()
    private lateinit var editor: TextEditorViewModel
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        editor = TextEditorViewModel(session, TextEditorRepository(gateway, session))
        store.put("editor", editor)
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login(): SessionState.Active {
        base.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://host.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `refresh of a new draft preserves content and format until explicit adoption`() = runTest {
        editor.start(login(), null, null)
        editor.destination = "/new"
        editor.edit(TextFieldValue("local draft")); editor.format("utf-16le")
        editor.reload(); advanceUntilIdle()
        assertEquals("local draft", editor.value.text); assertEquals("utf-16le", editor.encoding); assertTrue(editor.bom)
        assertNull(editor.baseline); assertEquals("remote", editor.latest?.content); assertTrue(editor.dirty)
        assertFalse(editor.busy); assertEquals(0, creates)
        editor.compareLatest()
        assertEquals("local draft", editor.value.text); assertEquals("remote", editor.baseline?.content)
        assertNull(editor.latest); assertTrue(editor.dirty)
    }
    @Test fun `known create rejection refreshes a corrected destination rather than the rejected path`() = runTest {
        editor.start(login(), null, null)
        editor.destination = "/rejected"; editor.edit(TextFieldValue("local"))
        onCreate = { ApiResult.Problem(403, "access-denied", null) }
        editor.save(); advanceUntilIdle()
        assertTrue(editor.failed); assertFalse(editor.unknown); assertNull(editor.verificationPath)
        editor.destination = " /corrected "
        editor.reload(); advanceUntilIdle()
        assertEquals(listOf("/corrected"), reads); assertEquals("local", editor.value.text)
        assertEquals(1, creates)
    }
    @Test fun `unknown save as fixes the verification target and prevents write replay`() = runTest {
        editor.start(login(), "/old", null); advanceUntilIdle()
        editor.edit(TextFieldValue("local")); editor.destination = "/new"
        editor.save(true); advanceUntilIdle()
        assertTrue(editor.unknown); assertEquals("/new", editor.verificationPath)
        editor.destination = "/other"; editor.save(true); advanceUntilIdle()
        assertEquals(1, creates)
        editor.reload(); advanceUntilIdle()
        assertEquals(listOf("/old", "/new"), reads); assertEquals("local", editor.value.text)
        assertTrue(editor.unknown); assertEquals("/new", editor.latest?.path)
    }
    @Test fun `matching readback resolves unknown create without another write`() = runTest {
        editor.start(login(), null, null)
        editor.destination = "/new"; editor.edit(TextFieldValue("local")); editor.format("utf-16le")
        editor.save(); advanceUntilIdle()
        assertTrue(editor.unknown); assertEquals(1, creates)
        onRead = { ApiResult.Success(file.copy(path = it, content = "local", encoding = "utf-16le", bom = true)) }
        editor.reload(); advanceUntilIdle()
        assertFalse(editor.unknown); assertFalse(editor.failed); assertFalse(editor.dirty); assertTrue(editor.saved)
        assertEquals("/new", editor.baseline?.path); assertEquals("", editor.destination)
        assertNull(editor.verificationPath); assertEquals(1, creates)
    }
    @Test fun `late read from a cleared identity cannot replace the new session draft`() = runTest {
        editor.start(login(), null, null)
        editor.destination = "/old-target"; editor.edit(TextFieldValue("old draft"))
        val entered = CompletableDeferred<Unit>(); val release = CompletableDeferred<Unit>()
        onRead = { path ->
            entered.complete(Unit)
            withContext(NonCancellable) { release.await() }
            ApiResult.Success(file.copy(path = path))
        }
        editor.reload(); runCurrent(); entered.await()
        session.clearSession(); runCurrent()
        assertEquals("", editor.value.text); assertEquals("", editor.destination); assertFalse(editor.busy)
        editor.start(login(), null, null)
        editor.destination = "/new-target"; editor.edit(TextFieldValue("new draft"))
        release.complete(Unit); advanceUntilIdle()
        assertEquals("new draft", editor.value.text); assertEquals("/new-target", editor.destination)
        assertNull(editor.baseline); assertNull(editor.latest); assertFalse(editor.failed); assertFalse(editor.unknown)
        assertEquals(0, creates)
    }
    @Test fun `read exception retains draft while create exception requires verification`() = runTest {
        editor.start(login(), null, null)
        editor.destination = "/new"; editor.edit(TextFieldValue("local"))
        onRead = { throw IllegalStateException("private detail") }
        editor.reload(); advanceUntilIdle()
        assertTrue(editor.failed); assertFalse(editor.unknown); assertFalse(editor.busy)
        assertEquals("local", editor.value.text); assertEquals("/new", editor.destination)
        onCreate = { throw IllegalStateException("private detail") }
        editor.save(); advanceUntilIdle()
        assertTrue(editor.failed); assertTrue(editor.unknown); assertFalse(editor.busy)
        assertEquals("/new", editor.verificationPath); assertEquals("local", editor.value.text)
        editor.save(); advanceUntilIdle(); assertEquals(1, creates)
    }
}
