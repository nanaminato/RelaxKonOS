package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.DockerRepository
import app.relaxkonos.mobile.data.OperationIndex
import app.relaxkonos.mobile.data.OperationIndexStorage
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
class OutboundProxyEditorTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val repository = DockerRepository(gateway, session, OperationIndex(object : OperationIndexStorage {
        override fun read(): ByteArray? = null
        override fun write(bytes: ByteArray) = Unit
    }))
    private val status get() = OutboundProxyWire.status(PROXY_STATUS)
    private suspend fun TestScope.editor(): OutboundProxyEditor {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        val owner = session.state.value as SessionState.Active
        gateway.onOutboundProxyStatus = { ApiResult.Success(status) }
        return OutboundProxyEditor(repository, owner, { session.state.value === owner }, this).also { it.load(); runCurrent() }
    }
    @Test fun `cancelled save or clear never submits while approved save uses captured unmasked draft`() = runTest {
        val editor = editor()
        var saves = 0
        var clears = 0
        gateway.onSaveOutboundProxy = { settings, confirmed ->
            saves++
            assertTrue(confirmed)
            assertEquals("http://secret:pw@host:8080", settings.httpProxy)
            ApiResult.Success(status.copy(settings = settings))
        }
        gateway.onClearOutboundProxy = { clears++; ApiResult.Success(status) }
        editor.change { it.copy(httpProxy = "http://secret:pw@host:8080") }
        editor.save()
        assertEquals(0, saves)
        editor.dismiss()
        editor.clear()
        editor.dismiss()
        assertEquals(0, clears)
        editor.save()
        editor.change { it.copy(httpProxy = "unexpected") }
        editor.confirm(null)
        runCurrent()
        assertEquals(1, saves)
        assertFalse(editor.dirty)
    }
    @Test fun `ambiguous write locks mutations until explicit authoritative refresh`() = runTest {
        val editor = editor()
        gateway.onClearOutboundProxy = { ApiResult.Transport("lost response") }
        editor.clear()
        editor.confirm(null)
        runCurrent()
        assertNull(editor.status)
        assertFalse(editor.canSubmit)
        editor.refresh()
        assertEquals(ProxyReview.Refresh, editor.review)
        editor.confirm(null)
        runCurrent()
        assertTrue(editor.canSubmit)
    }
    @Test fun `dirty refresh and back preserve draft until discard is accepted`() = runTest {
        val editor = editor()
        editor.change { it.copy(noProxy = "changed") }
        editor.refresh()
        editor.dismiss()
        assertEquals("changed", editor.draft!!.noProxy)
        var left = false
        editor.leave { left = true }
        assertFalse(left)
        editor.dismiss()
        editor.leave { left = true }
        editor.confirm { left = true }
        assertTrue(left)
    }
    @Test fun `owner invalidation prevents writes and old pending results from replacing the page`() = runTest {
        val editor = editor()
        val gate = CompletableDeferred<ApiResult<OutboundProxyStatus>>()
        gateway.onOutboundProxyStatus = { gate.await() }
        editor.load()
        runCurrent()
        session.clearSession()
        gate.complete(ApiResult.Success(status.copy(platform = "old result")))
        runCurrent()
        assertEquals("windows", editor.status!!.platform)
    }
    @Test fun `session switch during confirmation cannot save to either host`() = runTest {
        val editor = editor()
        var submissions = 0
        gateway.onSaveOutboundProxy = { _, _ -> submissions++; ApiResult.Success(status) }
        editor.save()
        session.clearSession()
        editor.confirm(null)
        runCurrent()
        assertEquals(0, submissions)
    }

    @Test fun `closing the page drops credential-bearing state and cannot reload`() = runTest {
        val editor = editor()
        editor.save()
        editor.close()
        assertNull(editor.status)
        assertNull(editor.draft)
        assertNull(editor.review)
        gateway.onOutboundProxyStatus = { error("closed editor must not load") }
        editor.load()
        runCurrent()
        assertNull(editor.draft)
    }

}
