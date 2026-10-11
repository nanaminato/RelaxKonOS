package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
class HostSettingsEditorTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val storage = object : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val journal = HostSettingsJournal(storage)
    private val repository = HostSettingsRepository(gateway, session,
        ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()),
            UsageMemoryStore(InMemoryUsageMemoryStorage())), journal)
    private val target = HostSettingsTarget("host/time", "hostMachine", null)
    private val revision = "A".repeat(64)
    private val facts = HostTimeSettings("UTC", listOf("UTC", "Asia/Tokyo"), revision,
        "2026-10-01T00:00:00Z", "review", target, HostSettingsCapability("available", null), "immediate")
    private val plan = HostSettingsPlan("ca57b835-8242-47bb-8a2b-c3a5f4658a6b", target, revision,
        "2099-10-01T00:00:00Z", emptyList(), "hostTimeChange", "host/time", "immediate", "")
    private suspend fun TestScope.editor(): HostSettingsEditor {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(server = it.server.copy(privilegedOperations = true)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://settings-review.invalid"), "review", "test".toCharArray()) {}
        val owner = session.state.value as SessionState.Active
        gateway.onHostTime = { ApiResult.Success(facts) }
        return HostSettingsEditor(owner, repository, { session.state.value === owner }, this, ElevationAnswerProvider.Declines)
            .also { it.lifecycle(true); runCurrent() }
    }
    @Test fun `failed refresh removes stale editable facts and can reload`() = runTest {
        val editor = editor()
        gateway.onHostTime = { ApiResult.Transport(null) }
        editor.load(); runCurrent()
        assertNull(editor.time)
        editor.value = "Asia/Tokyo"
        assertFalse(editor.canPreview)
        editor.discard()
        gateway.onHostTime = { ApiResult.Success(facts) }
        editor.load(); runCurrent()
        assertEquals(facts, editor.time)
    }
    @Test fun `leave confirmation preserves destination and cancellation keeps draft`() = runTest {
        val editor = editor(); var first = 0; var second = 0
        editor.value = "Asia/Tokyo"
        editor.leave { first++ }; editor.dismissLeave(); editor.confirmLeave()
        assertEquals("Asia/Tokyo", editor.value); assertEquals(0, first)
        editor.leave { second++ }; editor.confirmLeave(); editor.confirmLeave()
        assertEquals(0, first); assertEquals(1, second); assertFalse(editor.dirty)
    }
    @Test fun `backgrounding revokes a pending navigation confirmation`() = runTest {
        val editor = editor(); var navigations = 0
        editor.value = "Asia/Tokyo"; editor.leave { navigations++ }
        editor.lifecycle(false); editor.confirmLeave()
        editor.lifecycle(true); runCurrent(); editor.confirmLeave()
        assertEquals(0, navigations); assertFalse(editor.leaveReview)
    }
    @Test fun `cancelled refresh reports failure and invalidates snapshot`() = runTest {
        val editor = editor()
        gateway.onHostTime = { throw CancellationException("private detail") }
        editor.load(); runCurrent()
        assertFalse(editor.busy); assertNull(editor.time)
        assertEquals(R.string.error_connectivity, editor.error)
    }
    @Test fun `cancelled apply exposes original recovery reference without another write`() = runTest {
        val editor = editor(); var writes = 0
        gateway.onHostPreview = { _, _, _, _, _, _, _ -> ApiResult.Success(plan) }
        gateway.onHostApply = { _, _ -> writes++; throw CancellationException("private detail") }
        editor.value = "Asia/Tokyo"; editor.preview(); runCurrent()
        assertNotNull(editor.plan)
        editor.apply(); runCurrent()
        assertFalse(editor.busy)
        assertEquals(R.string.host_settings_unknown, editor.error)
        assertTrue(editor.references.single().unresolved)
        assertEquals(plan.id, editor.references.single().id)
        assertFalse(editor.canPreview)
        editor.apply(); runCurrent(); assertEquals(1, writes)
    }
}
