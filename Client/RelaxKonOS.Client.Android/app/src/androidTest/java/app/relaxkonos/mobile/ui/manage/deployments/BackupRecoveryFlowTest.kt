package app.relaxkonos.mobile.ui.manage.deployments

import android.os.SystemClock
import androidx.activity.ComponentActivity
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class BackupRecoveryFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var editor: BackupRecoveryEditor
    private val applicationId = "review-application"
    private val manifest = BackupManifest("review-backup", "review-operation", applicationId, "completed", "key-reference", emptyList(), null)
    private var reads = 0
    private var reconciliations = 0
    private val sentKeys = mutableListOf<String>()
    private val readKeys = mutableListOf<String>()
    private var onManifests: suspend () -> ApiResult<List<BackupManifest>> = { ApiResult.Success(listOf(manifest)) }
    private var onReconcile: suspend () -> ApiResult<BackupManifest?> = { throw IllegalStateException("private reconcile detail") }
    private var onCreate: suspend () -> ApiResult<BackupManifest> = { ApiResult.Transport(null) }
    private var preflights = 0
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private fun action(id: Int) = rule.onNode(hasClickAction() and hasText(text(id)))
    private fun show() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.BACKUP_RECOVERY)),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun backupManifests(serverUrl: String, accessToken: String, applicationId: String): ApiResult<List<BackupManifest>> {
                assertEquals(this@BackupRecoveryFlowTest.applicationId, applicationId); reads++; return onManifests()
            }
            override suspend fun definitionBackupRequest(serverUrl: String, accessToken: String, applicationId: String, idempotencyKey: String): ApiResult<BackupManifest?> {
                readKeys.add(idempotencyKey); reconciliations++; return onReconcile()
            }
            override suspend fun createDefinitionBackup(serverUrl: String, accessToken: String, applicationId: String, idempotencyKey: String): ApiResult<BackupManifest> {
                sentKeys.add(idempotencyKey); return onCreate()
            }
            override suspend fun backupPreflight(serverUrl: String, accessToken: String, backupId: String): ApiResult<BackupPreflight> {
                preflights++; return ApiResult.Success(BackupPreflight(backupId, applicationId, "review-restored", true, false, listOf("review blocker")))
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://backup-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val journal = BackupRecoveryRequestJournal(object : BackupRecoveryRequestStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        journal.begin(owner, applicationId)
        val repository = BackupRecoveryRepository(gateway, session, OperationIndex(object : OperationIndexStorage {
            override fun read(): ByteArray? = null
            override fun write(bytes: ByteArray) = Unit
        }), journal)
        rule.runOnUiThread { editor = BackupRecoveryEditor(session, repository, owner, applicationId, scope) }
        rule.setContent { MaterialTheme { Column(Modifier.verticalScroll(rememberScrollState())) { BackupRecoveryContent(editor) } } }
        rule.waitUntil(5_000) { !editor.state.loading && reads == 1 }
    }
    @After fun cleanup() { rule.runOnUiThread { scope.cancel() } }

    @Test fun reconciliationFailureRetainsListAndPendingKeyAndBusyRefreshBlocksWrites() {
        show()
        rule.onNodeWithText(text(R.string.backup_recovery_reconcile_failed)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.backup_recovery_unavailable)).assertDoesNotExist()
        action(R.string.backup_recovery_preflight).performScrollTo().assertIsEnabled()
        action(R.string.backup_recovery_retry_create).performScrollTo().assertIsEnabled()
        val receipt = CompletableDeferred<ApiResult<BackupManifest?>>()
        rule.runOnIdle { onReconcile = { receipt.await() } }
        action(R.string.common_refresh).performScrollTo().performClick()
        rule.waitUntil(5_000) { reconciliations == 2 }
        action(R.string.common_refresh).assertIsNotEnabled()
        action(R.string.backup_recovery_retry_create).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.backup_recovery_request_unknown)).performScrollTo().assertIsDisplayed()
        receipt.complete(ApiResult.Success(manifest))
        rule.waitUntil(5_000) { !editor.state.loading }
        rule.onNodeWithText(text(R.string.backup_recovery_reconcile_failed)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.backup_recovery_request_unknown)).assertDoesNotExist()
        action(R.string.backup_recovery_create).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertTrue(sentKeys.isEmpty()); assertEquals(1, readKeys.distinct().size); assertEquals(listOf(manifest), (editor.state.manifests as ApiResult.Success).value) }
    }

    @Test fun repeatedUnknownRetryShowsEachFailureAndReusesExactPendingKey() {
        show()
        repeat(2) { attempt ->
            action(R.string.backup_recovery_retry_create).performScrollTo().performClick()
            rule.waitUntil(5_000) { !editor.state.creating && sentKeys.size == attempt + 1 }
            rule.waitUntil(5_000) { rule.onAllNodes(hasClickAction() and hasText(text(R.string.common_dismiss))).fetchSemanticsNodes().size == 1 }
            rule.onNodeWithText(text(R.string.backup_recovery_create_failed)).assertIsDisplayed()
            action(R.string.common_dismiss).performClick(); rule.waitForIdle(); SystemClock.sleep(500)
        }
        rule.runOnIdle { assertTrue(editor.state.pendingRequest); assertEquals(readKeys.single(), sentKeys[0]); assertEquals(sentKeys[0], sentKeys[1]); assertEquals(1, reads) }
    }
    @Test fun successfulRetryClearsOldReconciliationWarning() {
        show()
        rule.runOnIdle { onCreate = { ApiResult.Success(manifest) } }
        action(R.string.backup_recovery_retry_create).performScrollTo().performClick()
        rule.waitUntil(5_000) { !editor.state.creating && editor.state.creation is ApiResult.Success }
        rule.onNodeWithText(text(R.string.backup_recovery_created)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.backup_recovery_reconcile_failed)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.backup_recovery_request_unknown)).assertDoesNotExist()
        rule.runOnIdle { assertEquals(1, sentKeys.size); assertEquals(readKeys.single(), sentKeys.single()) }
    }
    @Test fun repeatedBlockedPreflightShowsEachResultWithoutCreatingBackup() {
        show()
        repeat(2) { attempt ->
            action(R.string.backup_recovery_preflight).performScrollTo().performClick()
            rule.waitUntil(5_000) { preflights == attempt + 1 && editor.state.preflight != null }
            rule.waitUntil(5_000) { rule.onAllNodes(hasClickAction() and hasText(text(R.string.common_dismiss))).fetchSemanticsNodes().size == 1 }
            rule.onNodeWithText(text(R.string.backup_recovery_blocked) + "\nreview blocker").assertIsDisplayed()
            action(R.string.common_dismiss).performClick(); rule.waitForIdle(); SystemClock.sleep(500)
        }
        rule.runOnIdle { assertTrue(sentKeys.isEmpty()); assertTrue(editor.state.pendingRequest) }
    }
}
