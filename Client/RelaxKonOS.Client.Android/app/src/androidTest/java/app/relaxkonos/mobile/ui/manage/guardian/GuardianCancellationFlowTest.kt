package app.relaxkonos.mobile.ui.manage.guardian

import androidx.activity.ComponentActivity
import androidx.lifecycle.ViewModelStore
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.GuardianRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.*

class GuardianCancellationFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: GuardianViewModel
    private var cancelRead = false
    private var failDefinition = false
    private val definition = GuardianDefinition("job", "Job", "/bin/job", emptyList(), "/tmp", false, 15, 3, null, "review", "uid:1000")
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", emptySet(), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun guardianStatus(serverUrl: String, accessToken: String): ApiResult<GuardianStatus> {
                if (cancelRead) throw CancellationException("private guardian detail")
                return ApiResult.Success(GuardianStatus(true, true, "", "test"))
            }
            override suspend fun guardianWorkloads(serverUrl: String, accessToken: String): ApiResult<List<GuardianWorkload>> = ApiResult.Success(emptyList())
            override suspend fun guardianDefinition(serverUrl: String, accessToken: String, id: String): ApiResult<GuardianDefinitionResult> =
                if (failDefinition) ApiResult.Transport(null) else ApiResult.Success(GuardianDefinitionResult(true, "", definition))
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://guardian-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        rule.runOnUiThread { model = GuardianViewModel(session, GuardianRepository(gateway, session)); store.put("guardian", model) }
        rule.setContent { MaterialTheme { GuardianContent(owner, {}, model) } }
        rule.waitUntil(5_000) { !model.state.value.loading && !model.state.value.stale }
    }
    @Test fun cancelledRefreshDisablesCreationAndExplicitRefreshRecovers() {
        show()
        rule.onNodeWithText(text(R.string.guardian_create)).assertIsEnabled()
        rule.runOnIdle { cancelRead = true }
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.error }
        rule.onNodeWithText(text(R.string.guardian_failed)).assertIsDisplayed()
        rule.onNodeWithText("private guardian detail", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.guardian_stale)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.guardian_create)).assertIsNotEnabled()
        rule.runOnIdle { cancelRead = false }
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { !model.state.value.loading && !model.state.value.stale }
        rule.onNodeWithText(text(R.string.guardian_create)).assertIsEnabled()
    }
    @Test fun failedEditorReadDisablesSaveUntilActualReadButtonRecovers() {
        show()
        rule.runOnIdle { model.beginEdit(definition, false) }
        rule.waitUntil(5_000) { model.editing != null }
        val draft = model.editing
        rule.runOnIdle { failDefinition = true; model.readEditor() }
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.stale }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.common_save)).assertIsNotEnabled()
        rule.runOnIdle { org.junit.Assert.assertSame(draft, model.editing); failDefinition = false }
        rule.onNodeWithText(text(R.string.guardian_read_current)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.value.loading && !model.state.value.stale }
        rule.onNodeWithText(text(R.string.common_save)).assertIsEnabled()
    }
}
