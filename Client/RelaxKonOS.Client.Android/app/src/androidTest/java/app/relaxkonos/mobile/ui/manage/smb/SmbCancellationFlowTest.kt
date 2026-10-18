package app.relaxkonos.mobile.ui.manage.smb

import androidx.activity.ComponentActivity
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.Lifecycle
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import androidx.compose.ui.semantics.SemanticsActions
import androidx.test.espresso.Espresso
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SmbCancellationFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: SmbViewModel
    private var cancelRead = false
    private var writes = 0
    private var successfulWrite = false
    private var failVerification = false
    private var failFactsAfterInstallation = false
    private var readFailure: Exception? = null
    private var capabilityReads = 0
    private var currentShares = emptyList<SmbShare>()
    private val receipt = SmbReceipt("33333333-3333-3333-3333-333333333333", true, null)
    private val task = InstallationOperation("11111111-1111-1111-1111-111111111111", InstallationService.Smb,
        InstallationKind.Install, InstallationState.Running, InstallationStage.Installing, null, null, 1, 1, null, true)
    private val tasks = mutableMapOf(task.operationId to task)
    private val cancellations = mutableListOf<String>()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val facts = SmbFacts(SmbCapabilities(true, true, true, true, false, null),
        SmbStatus(SmbRuntimeState.Running, "4", true, true, null), emptyList(), emptyList(),
        SmbConnection("review-host", 445, "prefix", "smb://review-host"))
    private fun show(includeUser: Boolean = false) {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.FILE_SERVICES), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun smbCapabilities(serverUrl: String, accessToken: String): ApiResult<SmbCapabilities> {
                capabilityReads++
                readFailure?.let { throw it }
                if (cancelRead) throw CancellationException("private cancelled read")
                return ApiResult.Success(facts.capabilities)
            }
            override suspend fun smbStatus(serverUrl: String, accessToken: String) = ApiResult.Success(facts.status)
            override suspend fun smbConnection(serverUrl: String, accessToken: String) = ApiResult.Success(facts.connection)
            override suspend fun smbShares(serverUrl: String, accessToken: String): ApiResult<List<SmbShare>> = ApiResult.Success(currentShares)
            override suspend fun smbUsers(serverUrl: String, accessToken: String): ApiResult<List<SmbUser>> =
                ApiResult.Success(if (includeUser) listOf(SmbUser("review-user", true, true)) else emptyList())
            override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService): ApiResult<InstallationOperation?> = ApiResult.Success(null)
            override suspend fun installation(serverUrl: String, accessToken: String, operationId: String): ApiResult<InstallationOperation> {
                if (failFactsAfterInstallation) readFailure = IllegalStateException("private post-installation facts failure")
                return tasks[operationId]?.let { ApiResult.Success(it) } ?: ApiResult.Problem(404, "installation.missing", null)
            }
            override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<InstallationOperation> {
                cancellations += operationId
                val cancelled = requireNotNull(tasks[operationId]).copy(state = InstallationState.Cancelled, stage = InstallationStage.Cancelled, completedAtMillis = 2)
                tasks[operationId] = cancelled
                return ApiResult.Success(cancelled)
            }
            override suspend fun smbChange(serverUrl: String, accessToken: String, change: SmbChange, password: CharArray?): ApiResult<SmbReceipt> {
                writes++
                if (!successfulWrite) throw CancellationException("private cancelled write")
                change.share?.let { share ->
                    currentShares = listOf(SmbShare("opaque", share.name, share.path, share.description, share.readOnly,
                        share.enabled, share.guestAllowed, share.permissions, true, false))
                }
                if (failVerification) readFailure = IllegalStateException("private verification failure")
                return ApiResult.Success(receipt)
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://smb-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val index = OperationIndex(object : OperationIndexStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        rule.runOnUiThread {
            model = SmbViewModel(session, SmbRepository(gateway, session, elevation, SmbMutationJournal(Storage())),
                InstallationRepository(gateway, session, elevation, index, InstallationRequestJournal(Storage())), index, ElevationAnswerProvider.Declines)
            store.put("smb", model)
        }
        rule.setContent { MaterialTheme { SmbContent(model, owner, {}) } }
        rule.waitUntil(5_000) { model.state.facts != null && !model.state.busy }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun refresh() = rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
    @Test fun pausedPasswordConfirmationClearsInputsAndRejectsOldCallback() {
        show(includeUser = true)
        rule.onNodeWithText(text(R.string.workspace_users)).performClick()
        rule.onNodeWithText(text(R.string.smb_password)).performScrollTo().performClick()
        val old = requireNotNull(model.changeReview)
        fun field(id: Int) = rule.onNode(hasSetTextAction() and hasText(text(id)))
        field(R.string.smb_password).performTextInput("review-password")
        field(R.string.smb_password_again).performTextInput("review-password")
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread {
                assertNull(model.changeReview)
                val password = "review-password".toCharArray()
                model.change(old, password); assertTrue(password.all { it == '\u0000' }); assertEquals(0, writes)
            }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.smb_password)).performScrollTo().performClick()
        field(R.string.smb_password).assertTextEquals(text(R.string.smb_password), "")
        field(R.string.smb_password_again).assertTextEquals(text(R.string.smb_password_again), "")
        field(R.string.smb_password).performTextInput("review-password")
        field(R.string.smb_password_again).performTextInput("review-password")
        rule.onNodeWithText(text(R.string.smb_submit)).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.size == 1 }
        rule.runOnIdle { assertEquals(1, writes) }
    }
    @Test fun replacingInstallationWithdrawsOldCancelAndNewConfirmationTargetsNewTask() {
        show()
        val next = task.copy(operationId = "22222222-2222-2222-2222-222222222222")
        rule.runOnIdle { tasks[next.operationId] = next; model.recoverInstall(task.operationId, false) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installation?.operationId == task.operationId }
        rule.onAllNodesWithText(text(R.string.smb_records)).filter(hasClickAction()).onFirst().performClick()
        rule.onNodeWithText(text(R.string.operations_request_cancel)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        rule.runOnIdle { model.recoverInstall(next.operationId, false) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installation?.operationId == next.operationId }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertTrue(cancellations.isEmpty()) }
        rule.onNodeWithText(text(R.string.operations_request_cancel)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.common_cancel)) and hasAnyAncestor(isDialog())).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.installation?.state == InstallationState.Cancelled }
        rule.runOnIdle { assertEquals(listOf(next.operationId), cancellations); assertEquals(0, writes) }
    }
    private fun dismissFailure() {
        rule.onNodeWithText(text(R.string.common_dismiss)).assertIsDisplayed().performClick()
        rule.onNodeWithText(text(R.string.smb_unverified)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.smb_stop)).assertDoesNotExist()
    }
    @Test fun cancelledRefreshRemovesOldActionsAndExplicitRefreshRecovers() {
        show()
        rule.runOnIdle { cancelRead = true }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts == null }
        dismissFailure()
        rule.runOnIdle { cancelRead = false }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.smb_stop)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun cancelledStopRetainsMarkerAndRefreshDoesNotReplayWrite() {
        show()
        rule.onNodeWithText(text(R.string.smb_stop)).performScrollTo().performClick()
        rule.onNode(hasClickAction() and hasText(text(R.string.smb_submit))).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isNotEmpty() }
        dismissFailure()
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.smb_stop)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, writes); assertEquals(1, model.state.pending.size) }
    }
    private fun submitNewShare() {
        rule.onNodeWithText(text(R.string.workspace_shares)).performClick()
        rule.onNodeWithText(text(R.string.smb_create)).performScrollTo().performClick()
        rule.onNode(hasSetTextAction() and hasText(text(R.string.smb_name))).performScrollTo().performTextInput("review-share")
        rule.onNode(hasSetTextAction() and hasText(text(R.string.smb_path))).performScrollTo()
            .performTextInput("/srv/relaxkonos-shares/review-share")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.common_save)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.smb_submit)).performClick()
    }
    @Test fun successfulShareCreationClosesDraftWithOneVerification() {
        show(); successfulWrite = true
        val before = capabilityReads
        submitNewShare()
        rule.waitUntil(5_000) { !model.state.busy && model.state.saved == 1 }
        rule.onNodeWithText(text(R.string.common_save)).assertDoesNotExist()
        rule.onNodeWithText("review-share").performScrollTo().assertIsDisplayed()
        rule.runOnIdle {
            assertEquals(3, capabilityReads - before); assertEquals(1, writes)
            assertEquals(receipt, model.state.receipt); assertNull(model.state.problem); assertTrue(model.state.pending.isEmpty())
        }
    }
    @Test fun verifiedReceiptSurvivesReadFailureAndSubmittedShareCannotBeReplayed() {
        show(); successfulWrite = true; failVerification = true
        submitNewShare()
        rule.waitUntil(5_000) { !model.state.busy && model.state.saved == 1 }
        rule.onNodeWithText(text(R.string.smb_refresh_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_save)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.smb_records_attention)).performScrollTo().performClick()
        rule.onAllNodesWithText(context.getString(R.string.smb_receipt_id, receipt.operationId)).onFirst().performScrollTo().assertIsDisplayed()
        rule.runOnIdle { readFailure = null }
        rule.onNodeWithText(text(R.string.smb_accept_facts)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isEmpty() }
        rule.onNodeWithText(text(R.string.workspace_shares)).performClick()
        rule.onNodeWithText("review-share").performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, writes); assertEquals(receipt, model.state.receipt) }
    }
    @Test fun repeatedImmediateReadFailureReopensDismissedFeedback() {
        show()
        rule.runOnIdle { cancelRead = true; model.refresh() }
        rule.onNode(isDialog()).assertExists()
        val dismiss = rule.onNodeWithText(text(R.string.common_dismiss)).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.runOnIdle { dismiss(); model.refresh() }
        rule.onNode(isDialog()).assertExists()
        rule.onNodeWithText(text(R.string.common_dismiss)).assertIsDisplayed()
        rule.runOnIdle { assertNull(model.state.facts); assertFalse(model.state.busy); assertEquals(0, writes) }
    }

    @Test fun terminalInstallationRemainsVisibleWhenSharingFactsFailAndRefreshRecoversFacts() {
        show()
        val terminal = task.copy(state = InstallationState.Succeeded, stage = InstallationStage.Completed, completedAtMillis = 2, cancellable = false)
        rule.runOnIdle { tasks[task.operationId] = terminal; failFactsAfterInstallation = true; model.recoverInstall(task.operationId, false) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installation == terminal }
        rule.onNodeWithText(text(R.string.smb_installation_refresh_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onAllNodesWithText(text(R.string.smb_records)).filter(hasClickAction()).onFirst().performClick()
        rule.onNodeWithText(task.operationId).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.installation_state_succeeded)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.operations_request_cancel)).assertDoesNotExist()
        rule.runOnIdle { assertTrue(model.state.installationVerified); assertNull(model.state.facts); failFactsAfterInstallation = false; readFailure = null }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.runOnIdle { assertTrue(model.state.installationVerified); assertEquals(terminal, model.state.installation); assertEquals(0, writes); assertTrue(cancellations.isEmpty()) }
    }
    @Test fun failedInstallationKeepsItsActualReasonAfterSharingRefresh() {
        show()
        val failed = task.copy(state = InstallationState.Failed, stage = InstallationStage.Failed,
            problemCode = InstallationProblemCodes.FAILED, completedAtMillis = 2, cancellable = false)
        rule.runOnIdle { tasks[task.operationId] = failed; model.recoverInstall(task.operationId, false) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installation == failed }
        rule.onNodeWithText(text(R.string.installation_problem_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onAllNodesWithText(text(R.string.smb_records)).filter(hasClickAction()).onFirst().performClick()
        rule.onNodeWithText(text(R.string.installation_state_failed)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.installation_problem_failed)).performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertTrue(model.state.installationVerified); assertEquals(failed.problemCode, model.state.problem); assertEquals(0, writes) }
    }

}
