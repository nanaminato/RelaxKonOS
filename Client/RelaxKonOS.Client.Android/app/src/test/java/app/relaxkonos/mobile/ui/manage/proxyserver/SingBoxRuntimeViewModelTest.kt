package app.relaxkonos.mobile.ui.manage.proxyserver

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
import org.json.JSONObject
import org.junit.*
import org.junit.Assert.*

@OptIn(ExperimentalCoroutinesApi::class)
class SingBoxRuntimeViewModelTest {
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
    private val elevations = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage()))
    private val installations = InstallationRepository(gateway, session, elevations, index, InstallationRequestJournal(Storage()))
    private val store = ViewModelStore()
    private lateinit var model: SingBoxRuntimeViewModel
    private val host = """{"installation":{"installationId":"00112233-4455-6677-8899-aabbccddeeff","hostId":"host","runtimeId":"sing-box","ownership":"managed","recoveryRequired":false,"activeReleaseId":"release-a","stateRevision":3},"pendingOperationId":null,"measuredVersion":"1.13.21","problemCode":null,"environment":null,"installedReleases":[{"releaseId":"release-a"},{"releaseId":"release-b"}]}"""
    private val source = """{"runtimeId":"sing-box","baseUri":"https://release.invalid/","confirmationFingerprint":"fingerprint","identity":{"sourceId":"official","publisher":"Publisher","enabled":true,"trust":"confirmed","trustRevision":5}}"""
    private val release = """{"sourceId":"official","runtimeId":"sing-box","releaseId":"release-b","originalVersion":"1.14.3","trust":"confirmed","configurationSupported":true,"channel":"stable","target":{"os":"linux","architecture":"amd64","serviceHost":"systemd"},"evidence":"nativeConfiguration"}"""
    private val catalog get() = """{"source":$source,"fresh":true,"problemCode":null,"catalog":{"sourceId":"official","revision":4,"trustRevision":5,"signatureVerified":true,"releases":[$release]}}"""
    private val recovery = """{"required":false}"""
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = SingBoxRuntimeViewModel(session, ProxyServerRepository(gateway, session, index), installations, ElevationAnswerProvider.Declines)
        store.put("sing-box-runtime", model)
        gateway.onLogout = { _, _, _ -> ApiResult.Success(Unit) }
        gateway.onActiveInstallation = { ApiResult.Success(null) }
        gateway.onManagedRuntime = { _, route, _, _ -> ApiResult.Success(when (route) {
            ManagedRuntimeRoutes.installation("sing-box") -> host
            ManagedRuntimeRoutes.SOURCES -> "[$source]"
            ManagedRuntimeRoutes.recovery("sing-box") -> recovery
            else -> catalog
        }) }
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun TestScope.start() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.PROXY_SERVER), privilegedOperations = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://runtime.invalid"), "review", "synthetic".toCharArray()) {}
        runCurrent(); model.refresh(); advanceUntilIdle()
        model.catalog("official", false, false); advanceUntilIdle()
    }
    @Test fun `plan pins exact release state trust catalog and idempotency key`() = runTest {
        start()
        gateway.onManagedRuntime = { method, route, body, key ->
            assertEquals("POST", method); assertEquals(ManagedRuntimeRoutes.plan("sing-box"), route)
            val request = JSONObject(requireNotNull(body).toByteArray().decodeToString()).getJSONObject("request")
            assertEquals("release-b", request.getString("releaseId")); assertEquals(3, request.getLong("expectedStateRevision"))
            assertEquals(4, request.getLong("expectedCatalogRevision")); assertEquals(5, request.getLong("expectedTrustRevision"))
            assertEquals(key, request.getString("idempotencyKey"))
            ApiResult.Success(JSONObject().put("allowed", true).put("problems", org.json.JSONArray()).put("dependencies", JSONObject.NULL)
                .put("plan", JSONObject().put("runtimeId", "sing-box").put("request", request).put("release", JSONObject(release))).toString())
        }
        model.propose(InstallationKind.Upgrade, "release-b", false, true); advanceUntilIdle()
        assertEquals(InstallationService.SingBox, model.plan?.service); assertEquals(InstallationKind.Upgrade, model.plan?.kind)
    }
    @Test fun `unknown release never reaches plan API`() = runTest {
        start(); var calls = 0
        gateway.onManagedRuntime = { _, _, _, _ -> calls++; ApiResult.Transport(null) }
        model.propose(InstallationKind.Upgrade, "unknown", false, true); advanceUntilIdle()
        assertEquals(0, calls); assertNull(model.plan)
    }
    @Test fun `changed frozen request is refused`() = runTest {
        start()
        gateway.onManagedRuntime = { _, _, body, _ ->
            val request = JSONObject(requireNotNull(body).toByteArray().decodeToString()).getJSONObject("request").put("keepData", false)
            ApiResult.Success("""{"allowed":true,"problems":[],"dependencies":null,"plan":{"runtimeId":"sing-box","request":$request,"release":$release}}""")
        }
        model.propose(InstallationKind.Upgrade, "release-b", false, true); advanceUntilIdle()
        assertNull(model.plan); assertTrue(model.problems.isNotEmpty())
    }
    @Test fun `uninstall with data deletion requires no release source but preserves the explicit choice`() = runTest {
        start()
        gateway.onManagedRuntime = { _, _, body, _ ->
            val selection = JSONObject(requireNotNull(body).toByteArray().decodeToString()); assertTrue(selection.isNull("sourceId"))
            val request = selection.getJSONObject("request"); assertFalse(request.getBoolean("keepData")); assertTrue(request.isNull("releaseId"))
            ApiResult.Success("""{"allowed":true,"problems":[],"dependencies":null,"plan":{"runtimeId":"sing-box","request":$request,"release":null}}""")
        }
        model.propose(InstallationKind.Uninstall, null, false, false); advanceUntilIdle()
        assertEquals(InstallationKind.Uninstall, model.plan?.kind)
    }
    @Test fun `failed refresh removes host authority and old plan`() = runTest {
        start(); assertNotNull(model.installation)
        gateway.onManagedRuntime = { _, _, _, _ -> ApiResult.Transport(null) }
        model.refresh(); advanceUntilIdle()
        assertNull(model.installation); assertNull(model.catalog); assertTrue(model.sources.isEmpty()); assertNull(model.plan)
    }
    @Test fun `recovery close is refused while observed service is running`() = runTest {
        start(); val running = """{"required":true,"operationId":"00112233-4455-6677-8899-aabbccddeeff","recoveryProblemCode":"runtime.failed","serviceProblem":null,"service":{"running":true}}"""
        gateway.onManagedRuntime = { _, route, _, _ -> ApiResult.Success(when (route) {
            ManagedRuntimeRoutes.installation("sing-box") -> host
            ManagedRuntimeRoutes.SOURCES -> "[$source]"
            else -> running
        }) }
        model.refresh(); advanceUntilIdle(); var writes = 0
        gateway.onManagedRuntime = { _, _, _, _ -> writes++; ApiResult.Transport(null) }
        model.recover("close", running, true); advanceUntilIdle()
        assertEquals(0, writes)
    }
    @Test fun `session logout clears host authority choices plan and recovery`() = runTest {
        start(); assertNotNull(model.installation); assertNotNull(model.catalog)
        session.logout(); advanceUntilIdle()
        assertNull(model.owner); assertNull(model.installation); assertNull(model.catalog); assertNull(model.plan); assertNull(model.recovery)
        assertTrue(model.sources.isEmpty()); assertFalse(model.hasIntent)
    }
    @Test fun `uncertain submission locks selection and retries the exact frozen plan key`() = runTest {
        start()
        gateway.onManagedRuntime = { _, _, body, _ ->
            val request = JSONObject(requireNotNull(body).toByteArray().decodeToString()).getJSONObject("request")
            ApiResult.Success("""{"allowed":true,"problems":[],"dependencies":null,"plan":{"runtimeId":"sing-box","request":$request,"release":$release}}""")
        }
        model.propose(InstallationKind.Upgrade, "release-b", false, true); advanceUntilIdle()
        val frozen = requireNotNull(model.plan); val keys = mutableListOf<String>()
        gateway.onStartInstallation = { kind, request, key ->
            assertSame(frozen, request); assertEquals(InstallationKind.Upgrade, kind); keys += key
            if (keys.size == 1) ApiResult.Transport(null) else ApiResult.Success(InstallationOperation(
                "11111111-1111-1111-1111-111111111111", InstallationService.SingBox, kind, InstallationState.Running,
                InstallationStage.Downloading, 10, null, 1, 1, null, true))
        }
        model.submit(frozen); advanceUntilIdle()
        assertTrue(model.pending); assertTrue(model.hasIntent); assertSame(frozen, model.plan)
        model.clearPlan(); assertSame(frozen, model.plan)
        model.submit(frozen); advanceUntilIdle()
        assertEquals(listOf(frozen.key, frozen.key), keys); assertFalse(model.hasIntent); assertFalse(model.pending)
        assertEquals(InstallationService.SingBox, model.operation?.service)
    }
}
