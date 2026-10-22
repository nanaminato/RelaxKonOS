package app.relaxkonos.mobile.ui.manage.proxy

import android.content.Context
import android.content.res.Configuration
import android.graphics.Bitmap
import androidx.activity.ComponentActivity
import androidx.compose.material3.*
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalResources
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.ViewModelStore
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import java.io.File
import java.util.Locale
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import org.junit.*
import org.junit.Assert.*
import org.junit.runner.RunWith
import org.junit.runners.Parameterized

@RunWith(Parameterized::class)
class ProxyRuntimePresentationTest(private val language: String, private val dark: Boolean) {
    companion object {
        @JvmStatic @Parameterized.Parameters(name = "{0}-dark={1}")
        fun cases(): List<Array<Any>> = listOf("en", "zh", "ja").flatMap { language ->
            listOf(false, true).map { arrayOf<Any>(language, it) }
        }
    }
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: ProxyViewModel
    private lateinit var localized: Context
    private var ownership = "absent"
    private var trust = "confirmed"
    private val requests = mutableListOf<Pair<String, String>>()
    private val dependencies = listOf(
        Triple("platform", "linux/amd64", "satisfied"), Triple("serviceHost", "systemd", "missing"),
        Triple("diskSpace", "managed-data", "satisfied"), Triple("fileSystem", "atomic-rename", "unknown"),
        Triple("kernelFeature", "ipv6", "satisfied"))
    private fun storage() = object : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private fun text(id: Int) = localized.getString(id)
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun installation(): String = JSONObject().put("installation", JSONObject()
        .put("installationId", "00112233-4455-6677-8899-aabbccddeeff").put("hostId", "review-host")
        .put("runtimeId", "mihomo").put("ownership", ownership).put("stateRevision", 1)
        .put("activeReleaseId", if (ownership == "managed") "release-current" else JSONObject.NULL)
        .put("recoveryRequired", false)).put("pendingOperationId", JSONObject.NULL)
        .put("measuredVersion", JSONObject.NULL).put("problemCode", JSONObject.NULL)
        .put("environment", JSONObject().put("observations", JSONArray().apply {
            dependencies.forEach { (kind, identity, state) -> put(JSONObject().put("kind", kind).put("identity", identity)
                .put("state", state).put("requiredBytes", if (kind == "diskSpace") 2000 else JSONObject.NULL)
                .put("availableBytes", if (kind == "diskSpace") 8000 else JSONObject.NULL)) }
        })).toString()
    private fun sources() = JSONArray().put(JSONObject().put("runtimeId", "mihomo")
        .put("baseUri", "https://release.invalid/").put("confirmationFingerprint", "ab".repeat(32))
        .put("identity", JSONObject().put("sourceId", "review-source").put("publisher", "Review Publisher")
            .put("enabled", true).put("trust", trust))).toString()
    private val catalog = """{"source":{"identity":{"sourceId":"review-source"}},"fresh":true,"problemCode":null,"catalog":{"revision":1,"trustRevision":1,"signatureVerified":true,"releases":[{"runtimeId":"mihomo","releaseId":"release-reviewed","originalVersion":"1.19.30","trust":"confirmed","configurationSupported":true,"channel":"stable","target":{"os":"linux","architecture":"amd64","serviceHost":"systemd"},"evidence":"nativeConfiguration"}]}}"""
    private fun show() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val configuration = Configuration(context.resources.configuration).apply {
            setLocale(Locale.forLanguageTag(language))
            uiMode = (uiMode and Configuration.UI_MODE_NIGHT_MASK.inv()) or
                if (dark) Configuration.UI_MODE_NIGHT_YES else Configuration.UI_MODE_NIGHT_NO
        }
        localized = context.createConfigurationContext(configuration)
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.PROXY), true),
                    AuthTokens("review-access", "review-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun managedRuntime(serverUrl: String, accessToken: String, method: String, route: String,
                body: JsonBody?, key: String?): ApiResult<String> {
                requests += method to route
                check(method == "GET") { "Unexpected runtime mutation" }
                return ApiResult.Success(when (route) {
                    ManagedRuntimeRoutes.INSTALLATION -> installation()
                    ManagedRuntimeRoutes.SOURCES -> sources()
                    ManagedRuntimeRoutes.RECOVERY -> """{"required":false}"""
                    ManagedRuntimeRoutes.catalog("review-source", false, false) -> catalog
                    else -> error("Unexpected runtime route")
                })
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://runtime-review.invalid"), "review", "test".toCharArray()) {} }
        val index = OperationIndex(object : OperationIndexStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        val elevations = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        rule.runOnUiThread {
            model = ProxyViewModel(session, ProxyRepository(gateway, session, index, ProxyRequestJournal(storage())),
                InstallationRepository(gateway, session, elevations, index, InstallationRequestJournal(storage())), index, ElevationAnswerProvider.Declines)
            store.put("runtime", model)
        }
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalContext provides localized, LocalConfiguration provides configuration,
                LocalResources provides localized.resources, LocalDensity provides Density(density.density, 1.6f)) {
                MaterialTheme(colorScheme = if (dark) darkColorScheme() else lightColorScheme()) {
                    ProxyInstallEditor(model, {}, {})
                }
            }
        }
        rule.waitUntil(5_000) { !model.state.busy && model.state.runtime != null && requests.size == 3 }
    }
    private fun capture(suffix: String) {
        rule.waitForIdle()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        rule.waitUntil(5_000) { instrumentation.uiAutomation.rootInActiveWindow?.packageName?.toString() == instrumentation.targetContext.packageName }
        instrumentation.uiAutomation.waitForIdle(500, 5_000)
        val bitmap = instrumentation.uiAutomation.takeScreenshot()
        try { File(instrumentation.targetContext.getExternalFilesDir(null), "runtime-labels-$language-${if (dark) "dark" else "light"}-$suffix.png")
            .outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        } finally { bitmap.recycle() }
    }
    @Test fun localizedStatusAndEvidenceKeepUntrustedSourceAndMissingPlanBlocked() {
        show()
        rule.onNodeWithText(text(R.string.mihomo_state_notinstalled)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("absent", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.tunnels_confirm)).assertIsNotEnabled()
        capture("absent")
        rule.onNodeWithText("review-source", substring = true).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.runtimeCatalog != null }
        rule.onNodeWithText(text(R.string.managed_runtime_evidence_native), substring = true).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("nativeConfiguration", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.managed_runtime_preview)).performScrollTo().assertIsNotEnabled()
        rule.runOnUiThread { ownership = "managed"; trust = "revoked"; model.loadRuntime() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.runtime?.ownership == "managed" }
        val kinds = listOf(R.string.managed_runtime_dependency_platform, R.string.managed_runtime_dependency_host,
            R.string.managed_runtime_dependency_disk, R.string.managed_runtime_dependency_filesystem, R.string.managed_runtime_dependency_kernel)
        val states = listOf(R.string.managed_runtime_dependency_satisfied, R.string.managed_resource_missing,
            R.string.managed_runtime_dependency_satisfied, R.string.managed_resource_unknown, R.string.managed_runtime_dependency_satisfied)
        dependencies.forEachIndexed { i, (_, identity, _) ->
            rule.onNodeWithText(localized.getString(R.string.managed_runtime_dependency_observation, text(kinds[i]), identity, text(states[i])))
                .performScrollTo().assertIsDisplayed()
        }
        rule.onNodeWithText(text(R.string.managed_runtime_preview)).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText("review-source", substring = true).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.managed_runtime_trust_revoked), substring = true).assertIsDisplayed()
        val identity = localized.getString(R.string.managed_runtime_identity, text(R.string.managed_runtime_ownership_managed), text(R.string.managed_runtime_version_unmeasured))
        rule.onNodeWithText(identity).performScrollTo().assertIsDisplayed()
        capture("managed")
        rule.onNodeWithText(text(R.string.tunnels_confirm)).assertIsNotEnabled()
        rule.runOnIdle { assertTrue(requests.all { it.first == "GET" }); assertEquals(7, requests.size); assertFalse(model.hasIntent) }
    }
}
