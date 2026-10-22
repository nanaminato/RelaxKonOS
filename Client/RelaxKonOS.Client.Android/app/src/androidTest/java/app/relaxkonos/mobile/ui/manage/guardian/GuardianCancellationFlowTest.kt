package app.relaxkonos.mobile.ui.manage.guardian

import androidx.activity.ComponentActivity
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.Lifecycle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.semantics.SemanticsActions
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
    private lateinit var owner: SessionState.Active
    private var cancelRead = false
    private var failDefinition = false
    private var actions = 0
    private val workload = GuardianWorkload("job", "Job", "running", "running", 10, 0, null, 0,
        "/bin/job", "/tmp", false, "review", null, null)
    private val visible = mutableStateOf(true)
    private var delayWorkloads = false
    private var pendingWorkloads: kotlin.coroutines.Continuation<ApiResult<List<GuardianWorkload>>>? = null
    private val definition = GuardianDefinition("job", "Job", "/bin/job", emptyList(), "/tmp", false, 15, 3, null, "review", "uid:1000")
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun show(withWorkload: Boolean = false) {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", emptySet(), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun guardianStatus(serverUrl: String, accessToken: String): ApiResult<GuardianStatus> {
                delay(100)
                if (cancelRead) throw CancellationException("private guardian detail")
                return ApiResult.Success(GuardianStatus(true, true, "", "test"))
            }
            override suspend fun guardianWorkloads(serverUrl: String, accessToken: String): ApiResult<List<GuardianWorkload>> =
                if (delayWorkloads) kotlin.coroutines.suspendCoroutine { pendingWorkloads = it }
                else ApiResult.Success(if (withWorkload) listOf(workload) else emptyList())
            override suspend fun guardianLogs(serverUrl: String, accessToken: String, id: String): ApiResult<List<GuardianLog>> = ApiResult.Success(emptyList())
            override suspend fun guardianAction(serverUrl: String, accessToken: String, id: String, action: String): ApiResult<GuardianOperation> {
                actions++; return ApiResult.Success(GuardianOperation(true, ""))
            }
            override suspend fun guardianDefinition(serverUrl: String, accessToken: String, id: String): ApiResult<GuardianDefinitionResult> =
                if (failDefinition) ApiResult.Transport(null) else ApiResult.Success(GuardianDefinitionResult(true, "", definition))
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://guardian-review.invalid"), "review", "test".toCharArray()) {} }
        owner = session.state.value as SessionState.Active
        rule.runOnUiThread { model = GuardianViewModel(session, GuardianRepository(gateway, session)); store.put("guardian", model) }
        rule.setContent { MaterialTheme {
            if (visible.value) GuardianContent(owner, {}, model) else Text("Other page")
        } }
        rule.waitUntil(5_000) { !model.state.value.loading && !model.state.value.stale }
    }
    @Test fun cancelledRefreshDisablesCreationAndExplicitRefreshRecovers() {
        show()
        rule.onNodeWithText(text(R.string.guardian_empty)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.guardian_create)).assertIsEnabled()
        rule.runOnIdle { cancelRead = true }
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.error }
        rule.onNodeWithText(text(R.string.guardian_failed)).assertIsDisplayed()
        rule.onNodeWithText("private guardian detail", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.guardian_stale)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.guardian_empty)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.guardian_create)).assertIsNotEnabled()
        rule.runOnIdle { cancelRead = false; model.load(owner, reconcile = false) }
        rule.waitUntil(5_000) { !model.state.value.loading }
        rule.onNodeWithText(text(R.string.guardian_create)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.guardian_stale)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { !model.state.value.loading && !model.state.value.stale }
        rule.onNodeWithText(text(R.string.guardian_create)).assertIsEnabled()
        rule.onNodeWithText(text(R.string.guardian_empty)).assertIsDisplayed()
    }
    @Test fun pausedActionReviewIsWithdrawnAndFreshConfirmationExecutesOnce() {
        show(withWorkload = true)
        rule.runOnIdle { model.select("job") }
        rule.waitUntil(5_000) { model.state.value.definition != null }
        rule.onNodeWithText(text(R.string.guardian_restart)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        val old = model.actionReview!!
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { org.junit.Assert.assertNull(model.actionReview); model.confirmAction(old) }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.waitUntil(5_000) { !model.state.value.loading && !model.state.value.stale }
        rule.runOnIdle { org.junit.Assert.assertEquals(0, actions) }
        rule.onNodeWithText(text(R.string.guardian_restart)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.guardian_restart)) and hasAnyAncestor(isDialog()) and hasClickAction()).performClick()
        rule.waitUntil(5_000) { actions == 1 && !model.state.value.loading }
        rule.runOnIdle { model.confirmAction(old); org.junit.Assert.assertEquals(1, actions) }
    }
    @Test fun oldCancelCallbackCannotDismissReopenedActionReview() {
        show(withWorkload = true)
        rule.runOnIdle { model.select("job") }
        rule.waitUntil(5_000) { model.state.value.definition != null }
        rule.onNodeWithText(text(R.string.guardian_restart)).performScrollTo().performClick()
        val cancel = rule.onNode(hasText(text(R.string.common_cancel)) and hasAnyAncestor(isDialog()) and hasClickAction())
        val oldCancel = requireNotNull(cancel.fetchSemanticsNode().config[SemanticsActions.OnClick].action)
        cancel.performClick()
        rule.onNodeWithText(text(R.string.guardian_restart)).performScrollTo().performClick()
        val fresh = requireNotNull(model.actionReview)
        rule.runOnIdle { oldCancel(); org.junit.Assert.assertSame(fresh, model.actionReview) }
        rule.onNode(isDialog()).assertExists()
        rule.onNode(hasText(text(R.string.guardian_restart)) and hasAnyAncestor(isDialog()) and hasClickAction()).performClick()
        rule.waitUntil(5_000) { actions == 1 && !model.state.value.loading }
        rule.runOnIdle { oldCancel(); model.confirmAction(fresh); org.junit.Assert.assertEquals(1, actions) }
    }
    @Test fun leavingDuringNonCooperativeReadAllowsFreshPageToRecover() {
        show()
        rule.runOnIdle { delayWorkloads = true }
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { pendingWorkloads != null }
        rule.runOnIdle { visible.value = false }
        rule.onNodeWithText("Other page").assertIsDisplayed()
        rule.runOnIdle {
            org.junit.Assert.assertFalse(model.state.value.loading)
            org.junit.Assert.assertNull(model.state.value.status)
            delayWorkloads = false
            visible.value = true
        }
        rule.waitUntil(5_000) { model.state.value.loading }
        rule.runOnUiThread { pendingWorkloads!!.resumeWith(Result.success(ApiResult.Success(emptyList()))) }
        rule.waitUntil(5_000) { !model.state.value.loading && !model.state.value.stale }
        rule.onNodeWithText(text(R.string.guardian_create)).assertIsEnabled()
        rule.onNodeWithText(text(R.string.common_dismiss)).assertDoesNotExist()
        rule.runOnIdle { org.junit.Assert.assertFalse(model.state.value.error) }
    }
    @Test fun pausedActivityRevokesReadAndResumingRestoresCreation() {
        show()
        rule.runOnIdle { delayWorkloads = true }
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { pendingWorkloads != null }
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitUntil(5_000) { !model.state.value.loading && model.state.value.status == null }
        rule.runOnUiThread { delayWorkloads = false }
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(5_000) { model.state.value.loading }
        rule.runOnUiThread { pendingWorkloads!!.resumeWith(Result.success(ApiResult.Success(emptyList()))) }
        rule.waitUntil(5_000) { !model.state.value.loading && !model.state.value.stale }
        rule.onNodeWithText(text(R.string.guardian_create)).assertIsEnabled()
        rule.onNodeWithText(text(R.string.common_dismiss)).assertDoesNotExist()
        rule.runOnIdle { org.junit.Assert.assertFalse(model.state.value.error) }
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
