package app.relaxkonos.mobile.ui.manage.smb

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
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.FILE_SERVICES), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun smbCapabilities(serverUrl: String, accessToken: String): ApiResult<SmbCapabilities> {
                if (cancelRead) throw CancellationException("private cancelled read")
                return ApiResult.Success(facts.capabilities)
            }
            override suspend fun smbStatus(serverUrl: String, accessToken: String) = ApiResult.Success(facts.status)
            override suspend fun smbConnection(serverUrl: String, accessToken: String) = ApiResult.Success(facts.connection)
            override suspend fun smbShares(serverUrl: String, accessToken: String): ApiResult<List<SmbShare>> = ApiResult.Success(emptyList())
            override suspend fun smbUsers(serverUrl: String, accessToken: String): ApiResult<List<SmbUser>> = ApiResult.Success(emptyList())
            override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService): ApiResult<InstallationOperation?> = ApiResult.Success(null)
            override suspend fun installation(serverUrl: String, accessToken: String, operationId: String): ApiResult<InstallationOperation> =
                tasks[operationId]?.let { ApiResult.Success(it) } ?: ApiResult.Problem(404, "installation.missing", null)
            override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<InstallationOperation> {
                cancellations += operationId
                val cancelled = requireNotNull(tasks[operationId]).copy(state = InstallationState.Cancelled, stage = InstallationStage.Cancelled, completedAtMillis = 2)
                tasks[operationId] = cancelled
                return ApiResult.Success(cancelled)
            }
            override suspend fun smbChange(serverUrl: String, accessToken: String, change: SmbChange, password: CharArray?): ApiResult<SmbReceipt> {
                writes++; throw CancellationException("private cancelled write")
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
}
