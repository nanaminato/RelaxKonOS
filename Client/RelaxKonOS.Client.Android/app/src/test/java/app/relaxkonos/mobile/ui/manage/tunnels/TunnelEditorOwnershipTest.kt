package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.Assert.*

@OptIn(ExperimentalCoroutinesApi::class)
class TunnelEditorOwnershipTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val store = ViewModelStore()
    private val profile = TunnelProfile("22222222-2222-2222-2222-222222222222", "edge", "frp.example.test", 7000,
        TunnelAuth.None, false, TunnelTls.Default, TunnelRuntimeMode.Managed, null, 1, 1, 1)
    private var reads = 0
    private var probes = 0
    private val profileWrites = mutableListOf<TunnelProfileRequest>()
    private val definitionWrites = mutableListOf<TunnelDefinitionRequest>()
    private lateinit var model: TunnelsViewModel
    private fun storage() = object : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        gateway.onTunnelProfiles = { reads++; ApiResult.Success(listOf(profile)) }
        gateway.onTunnelDefinitions = { ApiResult.Success(emptyList()) }
        gateway.onTunnelRuntime = { ApiResult.Transport(null) }
        gateway.onManagedFrps = { ApiResult.Transport(null) }
        gateway.onActiveInstallation = { ApiResult.Success(null) }
        gateway.onTunnelDetect = { probes++; ApiResult.Transport(null) }
        gateway.onSaveTunnelProfile = { _, request -> profileWrites += request; ApiResult.Transport(null) }
        gateway.onSaveTunnelDefinition = { _, request -> definitionWrites += request; ApiResult.Transport(null) }
        val index = OperationIndex(object : OperationIndexStorage {
            override fun read(): ByteArray? = null
            override fun write(bytes: ByteArray) = Unit
        })
        val elevations = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage()))
        model = TunnelsViewModel(session, TunnelRepository(gateway, session, elevations, TunnelMutationJournal(storage())),
            InstallationRepository(gateway, session, elevations, index, InstallationRequestJournal(storage())), index,
            ElevationAnswerProvider.Declines, UploadDocumentOpener { null })
        store.put("tunnels", model)
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun signIn() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.TUNNELS), privilegedOperations = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://tunnel-review.invalid"), "alice", "test".toCharArray()) {}
    }

    @Test fun `discarded profile callbacks cannot change close query or save a new draft`() = runTest {
        signIn(); runCurrent(); model.refresh(); runCurrent(); model.editProfile()
        val old = requireNotNull(model.state.profileDraft)
        model.closeProfile(old); model.editProfile()
        val current = requireNotNull(model.state.profileDraft)
        val before = reads
        model.updateProfile(old, old.copy(name = "stale")); model.closeProfile(old)
        model.reloadProfile(old); model.detect(old); model.saveProfile(old); runCurrent()
        assertSame(current, model.state.profileDraft); assertEquals(before, reads)
        assertEquals(0, probes); assertTrue(profileWrites.isEmpty())
        model.updateProfile(current, current.copy(name = "current", host = "frp.example.test", mode = TunnelRuntimeMode.External, path = "/opt/frpc"))
        val changed = requireNotNull(model.state.profileDraft)
        model.saveProfile(current); model.closeProfile(current); assertSame(changed, model.state.profileDraft)
        model.detect(changed); runCurrent(); assertEquals(1, probes)
        model.saveProfile(changed); model.saveProfile(changed); runCurrent()
        assertEquals(listOf(changed.request()), profileWrites)
        assertSame(changed, model.state.profileDraft); assertEquals(1, model.state.pending.size)
        model.saveProfile(changed); runCurrent(); assertEquals(1, profileWrites.size)
    }

    @Test fun `definition callbacks retain the original draft and unknown request blocks replay`() = runTest {
        signIn(); runCurrent(); model.refresh(); runCurrent(); model.editDefinition(profile.id)
        val old = requireNotNull(model.state.definitionDraft)
        model.closeDefinition(old); model.editDefinition(profile.id)
        val current = requireNotNull(model.state.definitionDraft)
        val before = reads
        model.updateDefinition(old, old.copy(name = "stale")); model.closeDefinition(old)
        model.reloadDefinition(old); model.saveDefinition(old); runCurrent()
        assertSame(current, model.state.definitionDraft); assertEquals(before, reads); assertTrue(definitionWrites.isEmpty())
        model.updateDefinition(current, current.copy(name = "tcp", localPort = "8080", remotePort = "7001"))
        val changed = requireNotNull(model.state.definitionDraft)
        model.saveDefinition(current); assertTrue(definitionWrites.isEmpty())
        model.saveDefinition(changed); model.saveDefinition(changed); runCurrent()
        assertEquals(listOf(changed.request()), definitionWrites)
        assertSame(changed, model.state.definitionDraft); assertEquals(1, model.state.pending.size)
        model.saveDefinition(changed); runCurrent(); assertEquals(1, definitionWrites.size)
    }

    @Test fun `equal reloaded profile gets a fresh editor identity and rejects old callbacks`() = runTest {
        signIn(); runCurrent(); model.refresh(); runCurrent(); model.editProfile(profile)
        val old = requireNotNull(model.state.profileDraft)
        val version = model.state.editorVersion
        model.reloadProfile(old); runCurrent()
        val current = requireNotNull(model.state.profileDraft)
        assertEquals(old, current); assertNotSame(old, current); assertTrue(model.state.editorVersion > version)
        val before = reads
        model.closeProfile(old); model.reloadProfile(old); model.saveProfile(old); runCurrent()
        assertSame(current, model.state.profileDraft); assertEquals(before, reads); assertTrue(profileWrites.isEmpty())
    }

    @Test fun `new login cannot reuse a previous editor snapshot`() = runTest {
        signIn(); runCurrent(); model.refresh(); runCurrent(); model.editProfile(profile)
        val old = requireNotNull(model.state.profileDraft)
        signIn(); runCurrent(); model.refresh(); runCurrent(); model.editProfile(profile)
        val current = requireNotNull(model.state.profileDraft)
        model.updateProfile(old, old.copy(name = "stale")); model.saveProfile(old); model.closeProfile(old); runCurrent()
        assertSame(current, model.state.profileDraft); assertTrue(profileWrites.isEmpty())
    }

    @Test fun `equal reloaded definition also replaces the editor identity`() = runTest {
        val definition = TunnelDefinition("33333333-3333-3333-3333-333333333333", profile.id, "tcp", "alice",
            TunnelProtocol.Tcp, "127.0.0.1", 8080, 7001, null, true, false, false, 1, 1, 1, TunnelConnectionState.Disconnected, "")
        gateway.onTunnelDefinitions = { ApiResult.Success(listOf(definition)) }
        signIn(); runCurrent(); model.refresh(); runCurrent(); model.editDefinition(profile.id, definition)
        val old = requireNotNull(model.state.definitionDraft)
        val version = model.state.editorVersion
        model.reloadDefinition(old); runCurrent()
        val current = requireNotNull(model.state.definitionDraft)
        assertEquals(old, current); assertNotSame(old, current); assertTrue(model.state.editorVersion > version)
        val before = reads
        model.closeDefinition(old); model.reloadDefinition(old); model.saveDefinition(old); runCurrent()
        assertSame(current, model.state.definitionDraft); assertEquals(before, reads); assertTrue(definitionWrites.isEmpty())
    }
}
