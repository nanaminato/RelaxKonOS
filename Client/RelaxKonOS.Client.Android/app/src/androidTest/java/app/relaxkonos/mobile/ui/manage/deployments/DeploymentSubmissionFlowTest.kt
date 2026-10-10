package app.relaxkonos.mobile.ui.manage.deployments

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import java.io.File
import java.util.UUID
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class DeploymentSubmissionFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var browser: DeploymentBrowser
    private lateinit var owner: SessionState.Active
    private val first = DeploymentApplication("review-first", "review first", "image", "web", "running", "running", "http",
        2, null, 80, null, "127.0.0.1", null, null, null, null, null, "/", DeploymentLimits(null, null, null), emptyList(), emptyList(), "2026-10-01T00:00:00Z")
    private val second = first.copy(id = "review-second", name = "review second")
    private var listedApplications = listOf(first, second)
    private var snapshotMissing = false
    private val older = DeploymentRevision("review-older", 1, "image:old", false, null, null)
    private var receipt = CompletableDeferred<ApiResult<DeploymentOperation>>()
    private var sends = 0
    private val keys = mutableListOf<String>()
    private fun show() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS)),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun deploymentTemplates(serverUrl: String, accessToken: String): ApiResult<List<DeploymentTemplate>> = ApiResult.Success(emptyList())
            override suspend fun applicationCatalog(serverUrl: String, accessToken: String): ApiResult<List<CatalogTemplate>> = ApiResult.Success(emptyList())
            override suspend fun deploymentApplications(serverUrl: String, accessToken: String) = ApiResult.Success(listedApplications)
            override suspend fun deploymentSnapshot(serverUrl: String, accessToken: String, applicationId: String): ApiResult<DeploymentSnapshot> =
                if (snapshotMissing) ApiResult.Problem(404, "missing", null) else ApiResult.Success(
                    DeploymentSnapshot(if (applicationId == first.id) first else second, listOf(older), emptyList(), null))
            override suspend fun rollbackDeployment(serverUrl: String, accessToken: String, applicationId: String, revisionId: String, idempotencyKey: String): ApiResult<DeploymentOperation> {
                assertEquals(first.id, applicationId); assertEquals(older.id, revisionId); sends++; keys.add(idempotencyKey); return receipt.await()
            }
            override suspend fun deploymentLifecycle(serverUrl: String, accessToken: String, applicationId: String, action: DeploymentLifecycleAction, idempotencyKey: String): ApiResult<DeploymentOperation> {
                assertEquals(first.id, applicationId); sends++; return receipt.await()
            }
            override suspend fun deleteDeployment(serverUrl: String, accessToken: String, applicationId: String, idempotencyKey: String): ApiResult<DeploymentOperation> {
                assertEquals(first.id, applicationId); sends++; return receipt.await()
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://submission-review.invalid"), "review", "test".toCharArray()) {} }
        owner = session.state.value as SessionState.Active
        rule.runOnUiThread { browser = DeploymentBrowser(DeploymentRepository(gateway, session), session, scope) }
        rule.setContent { MaterialTheme {
            val state by browser.state.collectAsState()
            Column(Modifier.safeDrawingPadding()) {
                DeploymentControlRecovery(state, browser::retryControl)
                DeploymentList(state, browser::select, Modifier.weight(1f))
            }
        } }
        rule.waitUntil(5_000) { browser.state.value.applications is ApiResult.Success }
        rule.onNodeWithText(first.name).performScrollTo().performClick()
        rule.waitUntil(5_000) { browser.state.value.detail is ApiResult.Success }
    }
    @After fun cleanup() { rule.runOnUiThread { scope.cancel() } }
    @Test fun applicationRowsCannotSwitchDuringRollbackLifecycleOrDeleteAndRecoverAfterDefinitiveRejection() {
        show()
        repeat(3) { index ->
            rule.runOnIdle {
                receipt = CompletableDeferred()
                when (index) {
                    0 -> browser.rollback(older)
                    1 -> browser.lifecycle(DeploymentLifecycleAction.Restart)
                    else -> browser.delete()
                }
            }
            rule.waitUntil(5_000) { sends == index + 1 && browser.state.value.submitting }
            rule.onNodeWithText(second.name).performScrollTo().assert(hasClickAction().not())
            rule.runOnIdle { browser.select(second.id); browser.refresh(); assertEquals(first.id, browser.state.value.selectedId) }
            receipt.complete(ApiResult.Problem(403, "review-denied", null))
            rule.waitUntil(5_000) { !browser.state.value.submitting }
            rule.onNodeWithText(second.name).assertHasClickAction().performClick()
            rule.waitUntil(5_000) { browser.state.value.selectedId == second.id }
            rule.onNodeWithText(first.name).performScrollTo().performClick()
            rule.waitUntil(5_000) { browser.state.value.selectedId == first.id && browser.state.value.detail is ApiResult.Success }
        }
        rule.runOnIdle { assertEquals(3, sends) }
    }
    @Test fun unknownRollbackSurvivesRefreshAndSelectionAndExplicitRetryUsesOriginalKey() {
        show()
        rule.runOnIdle { browser.rollback(older) }
        rule.waitUntil(5_000) { sends == 1 }
        receipt.complete(ApiResult.Transport(null))
        rule.waitUntil(5_000) { !browser.state.value.submitting && browser.state.value.pendingControl != null }
        val retry = InstrumentationRegistry.getInstrumentation().targetContext.getString(R.string.deployments_control_retry)
        rule.onNodeWithText(retry).assertIsDisplayed()
        rule.onAllNodesWithText(InstrumentationRegistry.getInstrumentation().targetContext.getString(R.string.deployment_rollback)).onFirst().assertIsDisplayed()
        rule.onNodeWithText(older.id).assertIsDisplayed()
        rule.runOnIdle { browser.delete(); browser.lifecycle(DeploymentLifecycleAction.Restart); browser.refresh() }
        rule.waitUntil(5_000) { !browser.state.value.detailLoading }
        rule.onNodeWithText(second.name).performScrollTo().performClick()
        rule.waitUntil(5_000) { browser.state.value.selectedId == second.id }
        rule.onNodeWithText(retry).assertDoesNotExist()
        rule.onNodeWithText(first.name).performScrollTo().performClick()
        rule.waitUntil(5_000) { browser.state.value.pendingControl != null }
        rule.runOnIdle { assertEquals(1, sends); receipt = CompletableDeferred() }
        rule.onNodeWithText(retry).performScrollTo().performClick()
        rule.waitUntil(5_000) { sends == 2 && browser.state.value.submitting }
        rule.onNodeWithText(retry).assertIsNotEnabled()
        receipt.complete(ApiResult.Success(DeploymentOperation("review-operation", first.id, "rollback", "queued", "queued", null, null, null, null, true)))
        rule.waitUntil(5_000) { !browser.state.value.submitting && browser.state.value.pendingControl == null }
        rule.onNodeWithText(retry).assertDoesNotExist()
        rule.runOnIdle { assertEquals(2, sends); assertEquals(keys[0], keys[1]) }
    }
    @Test fun atomicRequestJournalRestoresExactReferenceOnAndroidFilesystem() {
        show()
        val directory = File(InstrumentationRegistry.getInstrumentation().targetContext.cacheDir, "deployment-control-review-${UUID.randomUUID()}")
        val request = PendingDeploymentControl(owner.serviceId, owner.userName, first.id, DeploymentControlKind.Rollback, older.id, "synthetic-original-key")
        try {
            DeploymentControlJournal(FileDeploymentControlStorage(directory)).begin(request)
            val restored = DeploymentControlJournal(FileDeploymentControlStorage(directory))
            assertEquals(request, restored.pending(owner, first.id))
            restored.complete(request)
            assertNull(DeploymentControlJournal(FileDeploymentControlStorage(directory)).pending(owner, first.id))
            rule.runOnIdle { assertEquals(0, sends) }
        } finally {
            File(directory, "deployment-control-requests.json").delete()
            File(directory, "deployment-control-requests.json.tmp").delete()
            directory.delete()
        }
    }
    @Test fun removedApplicationStillHasAnExplicitPendingRequestEntry() {
        show()
        rule.runOnIdle { browser.delete() }
        rule.waitUntil(5_000) { sends == 1 }
        receipt.complete(ApiResult.Transport(null))
        rule.waitUntil(5_000) { !browser.state.value.submitting && browser.state.value.pendingControl != null }
        rule.runOnIdle { listedApplications = emptyList(); snapshotMissing = true; browser.refresh() }
        rule.waitUntil(5_000) { !browser.state.value.loading && !browser.state.value.detailLoading }
        rule.onNodeWithText(first.name).assertDoesNotExist()
        rule.onNodeWithText(first.id).performScrollTo().assertHasClickAction().performClick()
        rule.waitUntil(5_000) { browser.state.value.pendingControl != null }
        rule.onNodeWithText(InstrumentationRegistry.getInstrumentation().targetContext.getString(R.string.deployments_control_retry)).performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, sends) }
    }
}
