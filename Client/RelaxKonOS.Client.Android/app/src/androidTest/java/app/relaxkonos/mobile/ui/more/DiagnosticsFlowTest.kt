package app.relaxkonos.mobile.ui.more

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.SystemRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class DiagnosticsFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var model: DiagnosticsController
    private lateinit var session: AuthSession
    private var check: suspend () -> ApiResult<Int> = { ApiResult.Success(3) }
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private fun login() = runBlocking {
        session.login(ServerConnectionIdentityRules.direct("https://diagnostics-review.invalid"), "review", "test".toCharArray()) {}
    }
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.FILES)),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
        }
        session = AuthSession(gateway); login()
        rule.runOnUiThread {
            model = DiagnosticsController(session, SystemRepository(gateway, session), { check() },
                { DiagnosticsDeviceFacts("test", 1, 2, false, "Unavailable", false) },
                { id, args -> context.getString(id, *args) }, scope)
        }
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                Box(Modifier.safeDrawingPadding().requiredSize(360.dp, 640.dp)) { DiagnosticsContent(model, null) }
            }
        } }
    }
    @After fun cleanup() { rule.runOnUiThread { scope.cancel() } }
    private fun runCheck() = rule.onNodeWithText(text(R.string.diagnostics_run)).performScrollTo().performClick()
    @Test fun busyCheckShowsLoadingAndDisablesRunAndExport() {
        val reply = CompletableDeferred<ApiResult<Int>>()
        check = { reply.await() }; show(); runCheck()
        try {
            rule.waitUntil(5_000) { model.running }
            rule.onNodeWithText(text(R.string.common_loading)).assertIsDisplayed()
            rule.onNodeWithText(text(R.string.diagnostics_not_run)).assertDoesNotExist()
            rule.onNodeWithText(text(R.string.diagnostics_run)).assertIsNotEnabled()
            rule.onNodeWithText(text(R.string.diagnostics_export)).performScrollTo().assertIsNotEnabled()
            rule.runOnIdle { model.buildExport(); assertNull(model.export); reply.complete(ApiResult.Success(3)) }
            rule.waitUntil(5_000) { !model.running }
        } finally { reply.complete(ApiResult.Success(3)) }
    }
    @Test fun providerExceptionIsReadableAndExplicitRetryRecovers() {
        check = { error("private diagnostics provider detail") }; show(); runCheck()
        rule.waitUntil(5_000) { !model.running && model.lines.isNotEmpty() }
        rule.onNodeWithText(text(R.string.diagnostics_files_transport)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("private diagnostics provider detail", substring = true).assertDoesNotExist()
        rule.runOnIdle { check = { ApiResult.Success(3) } }; runCheck()
        rule.waitUntil(5_000) { !model.running && model.lines.contains(context.getString(R.string.diagnostics_files_ok, 3)) }
        rule.onNodeWithText(context.getString(R.string.diagnostics_files_ok, 3)).performScrollTo().assertIsDisplayed()
    }
    @Test fun sameAddressNewLoginClosesOldExportAndClearsCheck() {
        show(); runCheck(); rule.waitUntil(5_000) { !model.running && model.lines.isNotEmpty() }
        rule.onNodeWithText(text(R.string.diagnostics_export)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.common_close)).assertIsDisplayed()
        rule.runOnIdle { assertFalse(model.export!!.contains("test-access")); login() }
        rule.waitUntil(5_000) { model.export == null && model.lines.isEmpty() }
        rule.onNodeWithText(text(R.string.common_close)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.diagnostics_not_run)).performScrollTo().assertIsDisplayed()
    }
}
