package app.relaxkonos.mobile.ui.servercenter

import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
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
import app.relaxkonos.mobile.servercenter.*
import kotlinx.coroutines.CancellationException
import org.junit.*
import org.junit.Assert.*

class ServerMaintenanceRecoveryFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private lateinit var model: ServerMaintenanceViewModel
    private var failRead = false
    private var failRecovery = false
    private val calls = mutableListOf<ServerDeploymentKind>()
    private val host = ServerHostTargetRules.create("review.invalid", 22, "review", "review-host", 1)
    private val journalDirectory by lazy { java.io.File(context.noBackupFilesDir, "maintenance-review-${java.util.UUID.randomUUID()}") }
    private fun journal() = MaintenancePendingJournal(FileMaintenancePendingStorage(journalDirectory))
    private var modelEpoch by androidx.compose.runtime.mutableIntStateOf(0)
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    @After fun cleanup() {
        rule.runOnUiThread { rule.activity.viewModelStore.clear() }
        java.io.File(journalDirectory, "server-maintenance-pending.bin").delete()
        java.io.File(journalDirectory, "server-maintenance-pending.bin.tmp").delete()
        journalDirectory.delete()
    }
    private fun createModel(): ServerMaintenanceViewModel = ServerMaintenanceViewModel(MaintenanceActions { request, _, started ->
                calls += request.kind
                request.recovery?.let { recovery ->
                    if (failRecovery) throw CancellationException("private recovery detail")
                    return@MaintenanceActions MaintenanceState(recovered = ServerDeploymentOperation(ServerDeploymentProtocol.VERSION,
                        recovery.reference.operationId, null, recovery.kind, ServerDeploymentPhase.Failed, ServerDeploymentState.Failed, 1, "review-time", false))
                }
                if (request.kind != ServerDeploymentKind.Status) {
                    started(MaintenancePending(ServerInstallOperationReference(request.hostId, "ssh-ed25519", ServerCenterHostKeyObservation("review.invalid", 22, "ssh-ed25519", byteArrayOf(1, 2, 3)).fingerprint,
                    "22222222-2222-2222-2222-222222222222", ServerHostPlatform.Linux, 1), request.kind)); throw CancellationException("private maintenance detail")
                }
                if (failRead) throw CancellationException("private read detail")
                MaintenanceState(snapshot = ServerHostSnapshot(installed = true, healthy = true, verifiedAtUtc = "review-time",
                    installationId = "11111111-1111-1111-1111-111111111111", mode = ServerInstallMode.LinuxUser, guardianEnabled = false, listenUrl = "https://127.0.0.1:5000"))
            }, journal(), kotlinx.coroutines.Dispatchers.Main.immediate)
    private fun show() {
        rule.runOnUiThread {
            model = createModel()
            rule.activity.viewModelStore.put("maintenance-review", model)
        }
        rule.setContent { MaintenanceTestContent() }
        rule.waitUntil(5_000) { calls.isNotEmpty() && !model.state.value.busy }
        rule.waitUntil(5_000) {
            InstrumentationRegistry.getInstrumentation().uiAutomation.rootInActiveWindow?.packageName?.toString() == context.packageName
        }
    }
    @Composable private fun MaintenanceTestContent() { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                Box(Modifier.safeDrawingPadding().requiredSize(360.dp, 640.dp)) { androidx.compose.runtime.key(modelEpoch) { ServerMaintenanceContent(host, model) } }
            }
        } }
    private fun acknowledge() {
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        InstrumentationRegistry.getInstrumentation().uiAutomation.waitForIdle(500, 5_000)
    }
    @Test fun pauseWithoutFrameWithdrawsRepairAndRejectsOldConfirmation() {
        show()
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().performClick()
        val submit = rule.onNode(hasText(text(R.string.server_maintenance_repair)) and hasClickAction() and hasAnyAncestor(isDialog()))
            .fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.STARTED)
            rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().performClick()
        rule.onAllNodes(hasSetTextAction())[0].performTextReplacement("synthetic-new-password")
        rule.runOnIdle { submit() }
        rule.waitForIdle()
        rule.onNode(isDialog()).assertExists()
        rule.onAllNodes(hasSetTextAction())[0].assert(SemanticsMatcher.expectValue(
            androidx.compose.ui.semantics.SemanticsProperties.InputText, androidx.compose.ui.text.AnnotatedString("synthetic-new-password")))
        rule.runOnIdle { assertTrue(calls.all { it == ServerDeploymentKind.Status }) }
    }
    @Test fun oldUninstallConfirmationCannotConsumeReopenedDialogAndFreshConfirmationRunsOnce() {
        show()
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_uninstall)).performScrollTo().performClick()
        val oldSubmit = rule.onNode(hasText(text(R.string.server_maintenance_uninstall)) and hasClickAction() and hasAnyAncestor(isDialog()))
            .fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        InstrumentationRegistry.getInstrumentation().uiAutomation.waitForIdle(500, 5_000)
        rule.onNodeWithText(text(R.string.server_maintenance_uninstall)).performScrollTo().performClick()
        rule.runOnIdle { oldSubmit() }
        rule.onNode(isDialog()).assertExists()
        rule.runOnIdle { assertTrue(calls.all { it == ServerDeploymentKind.Status }) }
        rule.onNodeWithText("11111111-1111-1111-1111-111111111111").assertIsDisplayed()
        rule.onNode(hasText(text(R.string.server_maintenance_uninstall)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_result_unknown)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, calls.count { it == ServerDeploymentKind.Uninstall }) }
    }
    @Test fun pausedConfigurationKeepsDraftAndRejectsOldSaveWhileFreshSaveRunsOnce() {
        show()
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_configuration_settings)).performScrollTo().performClick()
        rule.onAllNodes(hasSetTextAction() and hasAnyAncestor(isDialog()))[0].performTextReplacement("6000")
        val oldSave = rule.onNodeWithText(text(R.string.server_configuration_apply)).fetchSemanticsNode()
            .config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.STARTED)
            rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.onAllNodes(hasSetTextAction() and hasAnyAncestor(isDialog()))[0].assertTextContains("6000")
        rule.runOnIdle { oldSave() }
        rule.waitForIdle()
        rule.runOnIdle { assertEquals(0, calls.count { it == ServerDeploymentKind.Configure }) }
        rule.onNode(isDialog()).assertExists()
        rule.onAllNodes(hasSetTextAction() and hasAnyAncestor(isDialog()))[0].assertTextContains("6000")
        rule.onNodeWithText(text(R.string.server_configuration_apply)).performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_result_unknown)).assertIsDisplayed()
        rule.runOnIdle { oldSave() }
        rule.runOnIdle { assertEquals(1, calls.count { it == ServerDeploymentKind.Configure }) }
    }
    @Test fun componentDraftSurvivesPauseAndCancelledDiscardWithoutSending() {
        show()
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_configuration_components)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_guardian_choice)).performClick().assertIsOn()
        val oldSave = rule.onNodeWithText(text(R.string.server_configuration_apply)).fetchSemanticsNode()
            .config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.STARTED)
            rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.runOnIdle { oldSave() }
        rule.onNodeWithText(text(R.string.server_guardian_choice)).assertIsOn()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        val discardDialog = isDialog() and hasAnyDescendant(hasText(text(R.string.server_configuration_discard)))
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction() and hasAnyAncestor(discardDialog)).performClick()
        InstrumentationRegistry.getInstrumentation().uiAutomation.waitForIdle(500, 5_000)
        rule.onNodeWithText(text(R.string.server_guardian_choice)).assertIsOn()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText(text(R.string.server_configuration_discard_confirm)).performClick()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertEquals(0, calls.count { it == ServerDeploymentKind.Configure }) }
    }
    @Test fun activityRecreationRetainsConfigurationDraftAndRevokesOldSave() {
        show()
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_configuration_settings)).performScrollTo().performClick()
        rule.onAllNodes(hasSetTextAction() and hasAnyAncestor(isDialog()))[0].performTextReplacement("6000")
        val oldSave = rule.onNodeWithText(text(R.string.server_configuration_apply)).fetchSemanticsNode()
            .config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!
        val originalActivity = rule.activity
        rule.activityRule.scenario.recreate()
        rule.activityRule.scenario.onActivity { activity ->
            assertNotSame(originalActivity, activity)
            assertSame(model, activity.viewModelStore["maintenance-review"])
            activity.setContent { MaintenanceTestContent() }
        }
        rule.waitForIdle()
        rule.onAllNodes(hasSetTextAction() and hasAnyAncestor(isDialog()))[0].assertTextContains("6000")
        rule.runOnIdle { oldSave() }
        rule.waitForIdle()
        rule.onAllNodes(hasSetTextAction() and hasAnyAncestor(isDialog()))[0].assertTextContains("6000")
        rule.runOnIdle { assertTrue(calls.all { it == ServerDeploymentKind.Status }); assertEquals(1, calls.size) }
    }
    @Test fun unknownMaintenanceKeepsGateUntilOriginalReceiptIsReadWithoutReplay() {
        show()
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.server_maintenance_repair)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_result_unknown)).assertIsDisplayed(); acknowledge()
        rule.onNodeWithText(text(R.string.server_maintenance_check)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.installation_kind_upgrade)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { failRecovery = true }
        rule.onNodeWithText(text(R.string.server_maintenance_reconcile)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_read_failed)).assertIsDisplayed(); acknowledge()
        rule.onNodeWithText("22222222-2222-2222-2222-222222222222").performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.server_maintenance_pending)).performScrollTo().assertIsDisplayed()
        rule.runOnIdle { failRecovery = false }
        rule.onNodeWithText(text(R.string.server_maintenance_reconcile)).performScrollTo().performClick()
        rule.waitUntil(5_000) { model.state.value.pending == null && !model.state.value.busy }
        rule.onNodeWithText(text(R.string.server_maintenance_reconciled)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.server_maintenance_check)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(1, calls.count { it == ServerDeploymentKind.Repair }) }
    }
    @Test fun newlyCreatedModelRestoresFileGateAndVerifiedResolutionSurvivesAnotherModel() {
        show()
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.server_maintenance_repair)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_result_unknown)).assertIsDisplayed(); acknowledge()
        rule.runOnIdle {
            rule.activity.viewModelStore.clear(); model = createModel()
            rule.activity.viewModelStore.put("maintenance-review", model); modelEpoch++
        }
        rule.waitUntil(5_000) { model.state.value.pending != null && !model.state.value.busy }
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
        rule.onNodeWithText("22222222-2222-2222-2222-222222222222").performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.server_maintenance_reconcile)).performScrollTo().performClick()
        rule.waitUntil(5_000) { model.state.value.pending == null && !model.state.value.busy }
        rule.runOnIdle {
            rule.activity.viewModelStore.clear(); model = createModel()
            rule.activity.viewModelStore.put("maintenance-review", model); modelEpoch++
        }
        rule.waitUntil(5_000) { model.state.value.snapshot != null && !model.state.value.busy }
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertNull(model.state.value.pending); assertEquals(1, calls.count { it == ServerDeploymentKind.Repair }) }
    }
    @Test fun cancelledReadRestoresRefreshAndRepeatedImmediateFailureIsVisible() {
        failRead = true; show()
        rule.onNodeWithText(text(R.string.server_maintenance_read_failed)).assertIsDisplayed()
        val dismiss = rule.onNodeWithText(text(R.string.common_dismiss)).fetchSemanticsNode()
            .config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action!!
        rule.runOnIdle { dismiss(); model.check(host.hostId) }
        rule.onNodeWithText(text(R.string.server_maintenance_read_failed)).assertIsDisplayed()
        acknowledge()
        rule.runOnIdle { failRead = false }
        rule.onNodeWithText(text(R.string.server_maintenance_check)).performScrollTo().performClick()
        rule.waitUntil(5_000) { model.state.value.snapshot != null && !model.state.value.busy }
        rule.runOnIdle { assertTrue(calls.all { it == ServerDeploymentKind.Status }); assertNull(model.state.value.error) }
        rule.onNodeWithText("private read detail").assertDoesNotExist()
    }
    @Test fun cancelledConfirmedRepairShowsUnknownAndRefreshDoesNotReplay() {
        show()
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.server_maintenance_repair)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_result_unknown)).assertIsDisplayed()
        acknowledge()
        rule.runOnIdle {
            assertFalse(model.state.value.busy); assertFalse(model.state.value.complete); assertNull(model.state.value.snapshot)
            assertEquals(1, calls.count { it == ServerDeploymentKind.Repair })
        }
        rule.onNodeWithText(text(R.string.server_maintenance_check)).performScrollTo().performClick()
        rule.waitUntil(5_000) { model.state.value.snapshot != null && !model.state.value.busy }
        rule.runOnIdle { assertEquals(1, calls.count { it == ServerDeploymentKind.Repair }) }
        rule.onNodeWithText("private maintenance detail").assertDoesNotExist()
    }
}
