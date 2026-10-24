package app.relaxkonos.mobile.ui.manage.proxy

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Before
import org.junit.After
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ProxyViewModelTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val index = OperationIndex(object : OperationIndexStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    })
    private val elevation = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage()))
    private val journal = ProxyRequestJournal(Storage())
    private val repository = ProxyRepository(gateway, session, index, journal)
    private val installationJournal = InstallationRequestJournal(Storage())
    private val installations = InstallationRepository(gateway, session, elevation, index, installationJournal)
    private val store = ViewModelStore()
    private lateinit var model: ProxyViewModel
    private val operation = ProxyOperation("55555555-5555-5555-5555-555555555555", "start", ProxyOperationState.Running, "starting", "")
    private val install = InstallationOperation("44444444-4444-4444-4444-444444444444", InstallationService.Mihomo,
        InstallationKind.Install, InstallationState.Running, InstallationStage.Downloading, 20, null, 1, 1, null, true)
    private val overview = ProxyOverview(ProxyRuntime("managed", ProxyRuntimeState.Running, "1", null, true, ""),
        null, true, true, "healthy", "", false, true, false, "linux", false, ProxySystemProxyCapabilities(true, false, true, false), supportsLogs = true)
    private val sent = mutableListOf<String>()
    private var operationReads = 0
    private var installationReads = 0
    private var logReads = 0
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = ProxyViewModel(session, repository, installations, index, ElevationAnswerProvider.Declines)
        model.setInteractionActive(true)
        store.put("proxy", model)
        gateway.onProxyOverview = { ApiResult.Success(overview) }
        gateway.onProxyProfiles = { ApiResult.Success(emptyList()) }
        gateway.onProxySubscriptions = { ApiResult.Success(emptyList()) }
        gateway.onProxyRouting = { ApiResult.Success(ProxyRoutingMode.Rule) }
        gateway.onProxyDownloadOptions = { ApiResult.Success(false) }
        gateway.onProxySettings = { ApiResult.Success(ProxySettings(false, false, true, false, false, "info", 7890, false, "127.0.0.1", null, null)) }
        gateway.onProxyRecovery = { ApiResult.Success(ProxyRecovery(false, false, null, "")) }
        gateway.onRuntimeResources = { ApiResult.Success(ManagedRuntimeResources(null, emptyList(), "runtime.installation_absent")) }
        gateway.onActiveInstallation = { ApiResult.Success(install) }
        gateway.onInstallation = { installationReads++; ApiResult.Success(install) }
        gateway.onProxyOperation = { operationReads++; ApiResult.Success(operation) }
        gateway.onCancelInstallation = { id, _ -> sent += id; ApiResult.Transport(null) }
        gateway.onProxyLogs = { logReads++; ApiResult.Success(listOf(ProxyLog(1, "info", "review"))) }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.PROXY), privilegedOperations = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://review.invalid"), "review", "test".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `failed adoption does not reread and publish replacement facts`() = runTest {
        val owner = login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val pending = journal.begin(owner, null, null, ProxyWrite.Profile)
        model.refresh(); advanceUntilIdle()
        var reads = 0
        gateway.onProxyOverview = { reads++; if (reads == 1) ApiResult.Transport(null) else ApiResult.Success(overview) }
        adopt(pending); advanceUntilIdle()
        assertEquals(1, reads)
        assertNull(model.state.overview); assertNull(model.state.profiles); assertNull(model.state.observedAtMillis)
        assertTrue(model.state.uncertain); assertEquals(listOf(pending), model.state.pending)
    }
    @Test fun `repeated failed adoption preserves marker and explicit success can recover`() = runTest {
        val owner = login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val pending = journal.begin(owner, null, null, ProxyWrite.Profile)
        model.refresh(); advanceUntilIdle()
        var reads = 0
        gateway.onProxyOverview = { reads++; ApiResult.Transport(null) }
        adopt(pending); advanceUntilIdle()
        val version = model.state.requestVersion
        adopt(pending); advanceUntilIdle()
        assertTrue(model.state.requestVersion > version); assertEquals(2, reads)
        assertNull(model.state.overview); assertTrue(model.state.uncertain)
        assertEquals(listOf(pending), journal.pending(owner))
        gateway.onProxyOverview = { ApiResult.Success(overview) }
        adopt(pending); advanceUntilIdle()
        assertTrue(model.state.pending.isEmpty()); assertFalse(model.state.uncertain)
        assertEquals(ApiResult.Success(overview), model.state.overview)
    }
    @Test fun `cancelled adoption withdraws old core facts and keeps recovery gate`() = runTest {
        val owner = login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val pending = journal.begin(owner, null, null, ProxyWrite.Profile)
        model.refresh(); advanceUntilIdle()
        gateway.onProxyOverview = { throw CancellationException("private cancelled read") }
        adopt(pending); advanceUntilIdle()
        assertNull(model.state.overview); assertNull(model.state.settings); assertNull(model.state.observedAtMillis)
        assertTrue(model.state.uncertain); assertFalse(model.state.busy)
        assertEquals(listOf(pending), model.state.pending)
    }
    private fun adoption(pending: PendingProxyRequest): ProxyAdoptionReview {
        model.requestAdoption(model.interactionVersion, session.state.value as SessionState.Active, model.state.pending.single { it.key == pending.key })
        return requireNotNull(model.adoptionReview)
    }
    private fun adopt(pending: PendingProxyRequest) { model.accept(adoption(pending)) }
    private suspend fun TestScope.prepareAdoption(): ProxyAdoptionReview {
        val active = login(); runCurrent()
        val pending = journal.begin(active, null, null, ProxyWrite.Profile)
        model.refresh(); advanceUntilIdle()
        return adoption(pending)
    }
    @Test fun `adoption confirmation is one shot and stale close or submit cannot consume replacement`() = runTest {
        val old = prepareAdoption()
        model.dismissAdoption(old)
        val fresh = adoption(old.pending)
        model.dismissAdoption(old); model.accept(old); advanceUntilIdle()
        assertSame(fresh, model.adoptionReview); assertEquals(1, model.state.pending.size)
        model.accept(fresh); model.accept(fresh); advanceUntilIdle()
        assertTrue(model.state.pending.isEmpty()); assertNull(model.adoptionReview)
    }
    @Test fun `pause refresh and login invalidate adoption confirmation and old opener`() = runTest {
        val old = prepareAdoption()
        model.setInteractionActive(false); model.setInteractionActive(true)
        model.requestAdoption(old.version, old.owner, old.pending)
        assertNull(model.adoptionReview)
        model.accept(old); advanceUntilIdle(); assertEquals(1, model.state.pending.size)
        val fresh = adoption(old.pending)
        model.refresh(); advanceUntilIdle()
        model.accept(fresh); advanceUntilIdle()
        assertNull(model.adoptionReview); assertEquals(1, model.state.pending.size)
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.requestAdoption(model.interactionVersion, old.owner, model.state.pending.single())
        assertNull(model.adoptionReview)
        adopt(model.state.pending.single()); advanceUntilIdle()
        assertTrue(model.state.pending.isEmpty())
    }
    private suspend fun TestScope.ready(): SessionState.Active {
        val owner = login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.recoverOperation(operation.operationId); advanceUntilIdle()
        return owner
    }
    private val runtimeWire = """{"installation":{"installationId":"00112233-4455-6677-8899-aabbccddeeff","hostId":"host","runtimeId":"mihomo","ownership":"managed","stateRevision":3,"activeReleaseId":"release-a","recoveryRequired":false},"pendingOperationId":null,"measuredVersion":"1.19.10","problemCode":null}"""
    private val sourceWire = """[{"runtimeId":"mihomo","baseUri":"https://release.invalid/","confirmationFingerprint":"key","identity":{"sourceId":"official","publisher":"Publisher","enabled":true,"trust":"confirmed","trustRevision":5}}]"""
    private val catalogWire = org.json.JSONObject("""{"source":{"identity":{"sourceId":"official"}},"fresh":true,"problemCode":null,"catalog":{"revision":4,"trustRevision":5,"signatureVerified":true,"releases":[{"runtimeId":"mihomo","releaseId":"release-b","originalVersion":"1.19.30","trust":"confirmed","configurationSupported":true,"channel":"stable","target":{"os":"linux","architecture":"amd64","serviceHost":"systemd"},"evidence":"nativeConfiguration"}]}}""").apply {
        put("source", org.json.JSONArray(sourceWire).getJSONObject(0))
        getJSONObject("catalog").put("sourceId", "official").getJSONArray("releases").getJSONObject(0).put("sourceId", "official")
    }.toString()
    private suspend fun TestScope.runtimeReady() {
        login(); runCurrent()
        gateway.onManagedRuntime = { _, route, _, _ -> ApiResult.Success(when (route) {
            ManagedRuntimeRoutes.INSTALLATION -> runtimeWire
            ManagedRuntimeRoutes.SOURCES -> sourceWire
            ManagedRuntimeRoutes.RECOVERY -> """{"required":false}"""
            else -> catalogWire
        }) }
        model.loadRuntime(); advanceUntilIdle()
        model.loadRuntimeCatalog("official", false, false); advanceUntilIdle()
    }
    @Test fun `recovery callback cannot target a transaction absent from current observation`() = runTest {
        runtimeReady()
        var called = false
        gateway.onManagedRuntime = { _, _, _, _ -> called = true; ApiResult.Transport(null) }
        model.recoverRuntime("close", "00112233-4455-6677-8899-aabbccddeeff", true); advanceUntilIdle()
        assertFalse(called)
        model.recoverRuntime("stop", "00112233-4455-6677-8899-aabbccddeeff", false); advanceUntilIdle()
        assertFalse(called)
    }
    @Test fun `stale refresh discards catalog and prevents installation planning`() = runTest {
        runtimeReady(); assertNotNull(model.state.runtimeCatalog)
        var calls = 0
        gateway.onManagedRuntime = { _, _, _, _ -> calls++; ApiResult.Transport(null) }
        model.loadRuntimeCatalog("official", true, false); advanceUntilIdle()
        assertNull(model.state.runtimeCatalog); assertNull(model.state.runtimePlan)
        model.planRuntime(InstallationKind.Upgrade, "release-b", false, true); advanceUntilIdle()
        assertEquals(1, calls); assertNull(model.state.runtimePlan)
    }
    @Test fun `refused host plan displays blockers and cannot create ledger intent`() = runTest {
        runtimeReady()
        gateway.onManagedRuntime = { _, route, _, _ ->
            assertEquals(ManagedRuntimeRoutes.PLAN, route)
            ApiResult.Success("""{"allowed":false,"plan":null,"problems":["runtime.dependencies_unmet"],"dependencies":{"observations":[]}}""")
        }
        model.planRuntime(InstallationKind.Upgrade, "release-b", false, true); advanceUntilIdle()
        assertEquals(listOf("runtime.dependencies_unmet"), model.state.runtimeProblems)
        assertNotNull(model.state.runtimeDependencies); assertNull(model.state.runtimePlan); assertFalse(model.hasIntent)
        model.installRuntime(); advanceUntilIdle(); assertFalse(model.hasIntent)
    }
    @Test fun `host plan freezes exact selection and matching key without submitting automatically`() = runTest {
        runtimeReady()
        gateway.onManagedRuntime = { _, _, body, _ ->
            val request = org.json.JSONObject(requireNotNull(body).toByteArray().decodeToString()).getJSONObject("request")
            assertEquals("update", request.getString("change")); assertEquals("release-b", request.getString("releaseId"))
            val plan = org.json.JSONObject(RUNTIME_PLAN).put("request", request)
            ApiResult.Success(org.json.JSONObject().put("allowed", true).put("problems", org.json.JSONArray()).put("plan", plan).toString())
        }
        model.planRuntime(InstallationKind.Upgrade, "release-b", false, true); advanceUntilIdle()
        assertNotNull(model.state.runtimePlan); assertFalse(model.hasIntent)
        model.clearRuntimePlan(); assertNull(model.state.runtimePlan)
    }
    @Test fun `unknown ownership blocks overwrite before HTTP planning`() = runTest {
        runtimeReady()
        gateway.onManagedRuntime = { _, route, _, _ -> ApiResult.Success(when (route) {
            ManagedRuntimeRoutes.INSTALLATION -> runtimeWire.replace("managed", "unknown")
            ManagedRuntimeRoutes.SOURCES -> sourceWire
            else -> """{"required":false}"""
        }) }
        model.loadRuntime(); advanceUntilIdle()
        var called = false
        gateway.onManagedRuntime = { _, _, _, _ -> called = true; ApiResult.Transport(null) }
        model.planRuntime(InstallationKind.Uninstall, null, false, true); advanceUntilIdle()
        assertFalse(called); assertNull(model.state.runtimePlan)
    }
    private suspend fun TestScope.rejectCatalogSourceChange(trust: String, enabled: Boolean) {
        runtimeReady()
        val payload = org.json.JSONObject(catalogWire).apply {
            getJSONObject("source").getJSONObject("identity").put("trust", trust).put("enabled", enabled)
        }.toString()
        gateway.onManagedRuntime = { _, _, _, _ -> ApiResult.Success(payload) }
        model.loadRuntimeCatalog("official", false, false); advanceUntilIdle()
        assertEquals(trust, model.state.runtimeSources.single().trust)
        assertEquals(enabled, model.state.runtimeSources.single().enabled)
        var plans = 0
        gateway.onManagedRuntime = { _, _, _, _ -> plans++; ApiResult.Transport(null) }
        model.planRuntime(InstallationKind.Upgrade, "release-b", false, true); advanceUntilIdle()
        assertEquals(0, plans); assertNull(model.state.runtimePlan); assertFalse(model.hasIntent)
    }
    @Test fun `catalog source revocation replaces old trusted source and blocks planning`() = runTest {
        rejectCatalogSourceChange("revoked", true)
    }
    @Test fun `catalog source disable replaces old enabled source and blocks planning`() = runTest {
        rejectCatalogSourceChange("confirmed", false)
    }
    @Test fun `catalog from an older trust revision cannot generate a new plan`() = runTest {
        runtimeReady()
        val payload = org.json.JSONObject(catalogWire).apply {
            getJSONObject("source").getJSONObject("identity").put("trustRevision", 6)
        }.toString()
        gateway.onManagedRuntime = { _, _, _, _ -> ApiResult.Success(payload) }
        model.loadRuntimeCatalog("official", false, false); advanceUntilIdle()
        var plans = 0
        gateway.onManagedRuntime = { _, _, _, _ -> plans++; ApiResult.Transport(null) }
        model.planRuntime(InstallationKind.Upgrade, "release-b", false, true); advanceUntilIdle()
        assertEquals(0, plans); assertNull(model.state.runtimePlan)
    }
    @Test fun `managed uninstall does not inherit source or catalog revisions`() = runTest {
        runtimeReady()
        val payload = org.json.JSONObject(catalogWire).apply {
            getJSONObject("source").getJSONObject("identity").put("trust", "revoked")
        }.toString()
        gateway.onManagedRuntime = { _, _, _, _ -> ApiResult.Success(payload) }
        model.loadRuntimeCatalog("official", false, false); advanceUntilIdle()
        var plans = 0
        gateway.onManagedRuntime = { _, route, body, _ ->
            plans++; assertEquals(ManagedRuntimeRoutes.PLAN, route)
            val value = org.json.JSONObject(requireNotNull(body).toByteArray().decodeToString())
            assertTrue(value.isNull("sourceId"))
            val request = value.getJSONObject("request")
            assertEquals(0L, request.getLong("expectedCatalogRevision"))
            assertEquals(0L, request.getLong("expectedTrustRevision"))
            assertEquals("uninstall", request.getString("change")); assertTrue(request.getBoolean("keepData"))
            ApiResult.Success("""{"allowed":false,"plan":null,"problems":["runtime.dependencies_unmet"],"dependencies":null}""")
        }
        model.planRuntime(InstallationKind.Uninstall, null, false, true); advanceUntilIdle()
        assertEquals(1, plans); assertFalse(model.hasIntent)
    }
    @Test fun `session replacement clears frozen plan observations and recovery`() = runTest {
        runtimeReady(); assertNotNull(model.state.runtime)
        login(); runCurrent()
        assertNull(model.state.runtime); assertNull(model.state.runtimeCatalog); assertTrue(model.state.runtimeSources.isEmpty())
    }

    @Test fun `replacement installation rejects old callback and current confirmation sends once`() = runTest {
        val owner = ready()
        model.requestInstallationCancellation(model.interactionVersion, owner, install)
        val old = requireNotNull(model.installationCancellation)
        val replacement = install.copy(operationId = "66666666-6666-6666-6666-666666666666")
        gateway.onActiveInstallation = { ApiResult.Success(replacement) }
        model.refresh(); advanceUntilIdle(); model.cancelInstall(old); advanceUntilIdle()
        assertNull(model.installationCancellation); assertTrue(sent.isEmpty())
        model.requestInstallationCancellation(model.interactionVersion, owner, replacement)
        val current = requireNotNull(model.installationCancellation)
        model.cancelInstall(current); model.cancelInstall(current); advanceUntilIdle()
        assertEquals(listOf(replacement.operationId), sent); assertFalse(model.state.installationVerified)
    }
    @Test fun `reopening same task cannot revive dismissed confirmation`() = runTest {
        val owner = ready()
        model.requestInstallationCancellation(model.interactionVersion, owner, install)
        val old = requireNotNull(model.installationCancellation)
        model.dismissInstallationCancellation(old); model.requestInstallationCancellation(model.interactionVersion, owner, install)
        val current = requireNotNull(model.installationCancellation)
        model.cancelInstall(old); model.dismissInstallationCancellation(old)
        assertSame(current, model.installationCancellation); assertTrue(sent.isEmpty())
    }
    @Test fun `paused cancellation rejects old opener and submit after resume`() = runTest {
        val owner = ready()
        val version = model.interactionVersion
        model.requestInstallationCancellation(model.interactionVersion, owner, install)
        val old = requireNotNull(model.installationCancellation)
        model.setInteractionActive(false)
        assertNull(model.installationCancellation)
        model.cancelInstall(old); advanceUntilIdle(); assertTrue(sent.isEmpty())
        model.setInteractionActive(true)
        model.requestInstallationCancellation(version, owner, install)
        assertNull(model.installationCancellation)
        model.requestInstallationCancellation(model.interactionVersion, owner, install)
        val current = requireNotNull(model.installationCancellation)
        model.cancelInstall(old); assertSame(current, model.installationCancellation)
        model.cancelInstall(current); advanceUntilIdle(); assertEquals(listOf(install.operationId), sent)
    }

    @Test fun `change confirmation consumes only current instance once`() = runTest {
        val owner = ready(); var writes = 0
        model.requestChange(model.interactionVersion, owner, model.state) { writes++ }
        val old = requireNotNull(model.changeReview)
        model.dismissChange(old)
        model.requestChange(model.interactionVersion, owner, model.state) { writes++ }
        val current = requireNotNull(model.changeReview)
        model.dismissChange(old); model.submitChange(old)
        assertSame(current, model.changeReview); assertEquals(0, writes)
        model.submitChange(current); model.submitChange(current)
        assertNull(model.changeReview); assertEquals(1, writes)
    }
    @Test fun `latency sample completion does not invalidate unrelated change confirmation`() = runTest {
        val owner = ready(); var writes = 0
        val sample = CompletableDeferred<ApiResult<ProxyDelay>>()
        gateway.onTestProxyDelay = { _, _, _, _ -> sample.await() }
        model.testGroup(ProxyGroup("review-group", "Selector", "review-node", listOf("review-node")))
        runCurrent()
        model.requestChange(model.interactionVersion, owner, model.state) { writes++ }
        val review = requireNotNull(model.changeReview)
        sample.complete(ApiResult.Success(ProxyDelay("review-node", 12, false, ""))); advanceUntilIdle()
        assertNull(model.state.testingGroup); assertEquals(1, model.state.delays.size)
        model.submitChange(review); assertEquals(1, writes); assertNull(model.changeReview)
    }
    @Test fun `change confirmation rejects paused opener old state and old login`() = runTest {
        val owner = ready(); var writes = 0
        val version = model.interactionVersion
        val observed = model.state
        model.requestChange(version, owner, observed) { writes++ }
        val old = requireNotNull(model.changeReview)
        model.setInteractionActive(false); assertNull(model.changeReview)
        model.submitChange(old); model.requestChange(version, owner, observed) { writes++ }
        assertNull(model.changeReview)
        model.setInteractionActive(true)
        model.requestChange(version, owner, observed) { writes++ }; model.submitChange(old)
        assertNull(model.changeReview)
        model.refresh(); advanceUntilIdle()
        model.requestChange(model.interactionVersion, owner, observed) { writes++ }
        assertNull(model.changeReview)
        model.requestChange(model.interactionVersion, owner, model.state) { writes++ }
        val refreshReview = requireNotNull(model.changeReview)
        model.refresh(); advanceUntilIdle(); model.submitChange(refreshReview)
        assertNull(model.changeReview)
        model.requestChange(model.interactionVersion, owner, model.state) { writes++ }
        val loginReview = requireNotNull(model.changeReview)
        login(); runCurrent(); model.submitChange(loginReview)
        model.requestChange(model.interactionVersion, owner, model.state) { writes++ }
        assertNull(model.changeReview); assertEquals(0, writes)
    }

    private fun recovery(owner: SessionState.Active, installation: Boolean = false): ProxyRecoveryForm {
        model.requestRecovery(model.interactionVersion, owner, model.state, installation)
        return requireNotNull(model.recoveryForm)
    }
    @Test fun `recovery owns form draft and one query`() = runTest {
        val owner = ready(); val before = operationReads
        val old = recovery(owner); val version = model.interactionVersion
        model.changeRecoveryId(old, version, operation.operationId)
        val oldDraft = old.draft
        model.dismissRecovery(old, version)
        val fresh = recovery(owner)
        model.changeRecoveryId(fresh, version, operation.operationId)
        model.dismissRecovery(old, version); model.submitRecovery(old, version, oldDraft)
        assertSame(fresh, model.recoveryForm); assertEquals(before, operationReads)
        val stale = fresh.draft
        model.changeRecoveryId(fresh, version, install.operationId)
        model.submitRecovery(fresh, version, stale); assertEquals(before, operationReads)
        model.changeRecoveryId(fresh, version, operation.operationId)
        val current = fresh.draft
        model.submitRecovery(fresh, version, current); model.submitRecovery(fresh, version, current); advanceUntilIdle()
        assertNull(model.recoveryForm); assertEquals(before + 1, operationReads)
    }
    @Test fun `recovery pause preserves draft but old interactions refresh login and exit revoke ownership`() = runTest {
        val owner = ready(); val before = operationReads
        val form = recovery(owner); val version = model.interactionVersion
        model.changeRecoveryId(form, version, operation.operationId)
        val draft = form.draft
        model.setInteractionActive(false)
        model.changeRecoveryId(form, version, "changed"); model.dismissRecovery(form, version); model.submitRecovery(form, version, draft)
        assertSame(form, model.recoveryForm); assertSame(draft, form.draft)
        model.setInteractionActive(true)
        model.submitRecovery(form, version, draft); assertEquals(before, operationReads)
        model.refresh(); advanceUntilIdle(); assertNull(model.recoveryForm)
        model.submitRecovery(form, model.interactionVersion, draft); assertEquals(before + 1, operationReads)
        recovery(owner); model.leavePage(); assertNull(model.recoveryForm)
        model.setInteractionActive(true); val replaced = recovery(owner)
        login(); runCurrent(); model.submitRecovery(replaced, model.interactionVersion, replaced.draft)
        assertNull(model.recoveryForm); assertEquals(before + 1, operationReads)
    }
    @Test fun `installation recovery cannot identify replacement pending request`() = runTest {
        val owner = ready()
        val pending = installationJournal.begin(owner, InstallationService.Mihomo, InstallationKind.Install, "first")
        model.refresh(); advanceUntilIdle()
        val form = recovery(owner, true); assertEquals(pending, form.pending)
        model.changeRecoveryId(form, model.interactionVersion, install.operationId)
        model.identifyRecovery(form, model.interactionVersion, form.draft, true)
        installationJournal.complete(pending)
        val replacement = installationJournal.begin(owner, InstallationService.Mihomo, InstallationKind.Uninstall, "replacement")
        val before = installationReads
        model.submitRecovery(form, model.interactionVersion, form.draft); advanceUntilIdle()
        assertEquals(before, installationReads); assertEquals(listOf(replacement), installationJournal.pending(owner))
        assertTrue(model.state.uncertain); assertNull(model.recoveryForm)
    }
    @Test fun `installation recovery requires explicit identification of original pending request`() = runTest {
        val owner = ready()
        val pending = installationJournal.begin(owner, InstallationService.Mihomo, InstallationKind.Install, "original")
        model.refresh(); advanceUntilIdle()
        val form = recovery(owner, true); val before = installationReads
        model.submitRecovery(form, model.interactionVersion, form.draft); advanceUntilIdle()
        assertSame(form, model.recoveryForm); assertEquals(before, installationReads)
        model.identifyRecovery(form, model.interactionVersion, form.draft, true)
        val oldConsent = form.draft
        model.changeRecoveryId(form, model.interactionVersion, operation.operationId)
        assertFalse(form.draft.identified)
        model.identifyRecovery(form, model.interactionVersion, oldConsent, true)
        model.submitRecovery(form, model.interactionVersion, form.draft); advanceUntilIdle()
        assertSame(form, model.recoveryForm); assertEquals(before, installationReads)
        model.changeRecoveryId(form, model.interactionVersion, install.operationId)
        model.identifyRecovery(form, model.interactionVersion, form.draft, true)
        val draft = form.draft
        model.submitRecovery(form, model.interactionVersion, draft); model.submitRecovery(form, model.interactionVersion, draft); advanceUntilIdle()
        assertEquals(before + 1, installationReads); assertTrue(installationJournal.pending(owner).isEmpty())
        assertTrue(model.state.installationVerified); assertFalse(model.state.pendingInstallation); assertTrue(sent.isEmpty())
    }

    @Test fun `same address new login clears tasks and rejects old confirmation`() = runTest {
        val owner = ready(); model.requestInstallationCancellation(model.interactionVersion, owner, install)
        val old = requireNotNull(model.installationCancellation)
        login(); runCurrent(); model.cancelInstall(old); advanceUntilIdle()
        assertNull(model.state.installation); assertNull(model.installationCancellation); assertTrue(sent.isEmpty())
    }
    @Test fun `one foreground observation reads both active task types`() = runTest {
        ready(); val webBefore = operationReads; val installBefore = installationReads
        model.observeTasks { true }; advanceUntilIdle()
        assertEquals(webBefore + 1, operationReads); assertEquals(installBefore + 1, installationReads)
    }
    @Test fun `delayed observation rechecks foreground and manual read generation`() = runTest {
        ready(); val before = operationReads
        var visible = true
        val observation = launch { model.observeTasks { visible } }; runCurrent()
        visible = false; advanceTimeBy(1600); runCurrent(); observation.join()
        assertEquals(before, operationReads)
        val old = launch { model.observeTasks { true } }; runCurrent()
        model.pollInstall(); runCurrent(); val after = installationReads
        advanceTimeBy(1600); runCurrent(); old.join()
        assertEquals(after, installationReads); assertEquals(before, operationReads)
    }
    @Test fun `cancelled refresh clears old facts and cannot retain verified installation`() = runTest {
        ready(); gateway.onProxyProfiles = { throw CancellationException("isolated cancellation") }
        model.refresh(); advanceUntilIdle()
        assertNull(model.state.overview); assertNull(model.state.profiles); assertNull(model.state.observedAtMillis)
        assertFalse(model.state.installationVerified); assertFalse(model.state.busy)
    }
    @Test fun `stopped diagnostics cannot publish late sample or clear newer busy state`() = runTest {
        ready()
        val old = CompletableDeferred<ApiResult<List<ProxyLog>>>()
        val fresh = CompletableDeferred<ApiResult<List<ProxyLog>>>()
        gateway.onProxyLogs = { withContext(NonCancellable) { old.await() } }
        model.diagnostics("logs"); runCurrent(); assertTrue(model.state.diagnosticsBusy)
        model.stopDiagnostics()
        gateway.onProxyLogs = { fresh.await() }
        model.diagnostics("logs"); runCurrent()
        old.complete(ApiResult.Success(listOf(ProxyLog(1, "info", "late")))); runCurrent()
        assertNull(model.state.logs); assertTrue(model.state.diagnosticsBusy)
        fresh.complete(ApiResult.Success(listOf(ProxyLog(2, "info", "fresh")))); advanceUntilIdle()
        assertEquals("fresh", (model.state.logs as ApiResult.Success).value.single().message)
        assertFalse(model.state.diagnosticsBusy)
    }
    @Test fun `diagnostic failure retries only while original page remains foreground`() = runTest {
        ready(); gateway.onProxyLogs = { logReads++; ApiResult.Transport(null) }
        var visible = true
        val observing = launch { model.observeDiagnostics("logs") { visible } }
        runCurrent(); advanceTimeBy(6500); runCurrent()
        assertEquals(3, logReads); assertTrue(model.state.diagnosticsFailed)
        visible = false; advanceTimeBy(3500); runCurrent(); observing.join()
        assertEquals(3, logReads)
        gateway.onProxyLogs = { logReads++; ApiResult.Success(emptyList()) }
        model.diagnostics("logs"); advanceUntilIdle()
        assertEquals(4, logReads); assertFalse(model.state.diagnosticsFailed)
    }
}
