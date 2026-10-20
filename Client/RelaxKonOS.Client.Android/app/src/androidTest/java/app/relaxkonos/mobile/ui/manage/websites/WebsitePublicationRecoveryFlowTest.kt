package app.relaxkonos.mobile.ui.manage.websites

import androidx.compose.foundation.verticalScroll
import androidx.activity.ComponentActivity
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.Lifecycle
import androidx.compose.runtime.mutableStateOf
import androidx.compose.material3.Text
import app.relaxkonos.mobile.ui.common.WorkspaceSection
import androidx.compose.material3.MaterialTheme
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
import org.junit.*
import org.junit.Assert.*

class WebsitePublicationRecoveryFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: WebsitesViewModel
    private var failure: Exception? = null
    private var historyFailure: Exception? = null
    private var historyReads = 0
    private val operationIds = mutableListOf<String>()
    private var repeatedState = false
    private var keepRunning = false
    private val visible = mutableStateOf(true)
    private val operation = WebsitePublicationOperation("11111111-1111-1111-1111-111111111111", "application-a", "server", "example.test",
        null, null, null, "succeeded", "completed", "", null, emptyList(), null, null, null)
    private var history = listOf(operation)
    private var applications = emptyList<DeploymentApplication>()
    private var recordsPage = false
    private var seedPending = false
    private var failPublicationStorage = false
    private lateinit var publicationStorage: Storage
    private lateinit var publicationJournal: WebsitePublicationJournal
    private val stoppedApplication = DeploymentApplication("application-b", "Stopped history target", "image", "web", "stopped", "stopped", "http",
        1, null, 80, null, "127.0.0.1", null, null, null, null, null, "/", DeploymentLimits(null, null, null), emptyList(), emptyList(), "2026-10-01T00:00:00Z")
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        var failWrites = false
        override fun write(bytes: ByteArray) { check(!failWrites) { "private storage failure" }; this.bytes = bytes.copyOf() }
    }
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.WEB_SERVER, ServerCapabilities.APPLICATION_DEPLOYMENTS), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun webServers(serverUrl: String, accessToken: String): ApiResult<List<WebServer>> {
                failure?.let { throw it }
                return ApiResult.Success(emptyList())
            }
            override suspend fun websitePublication(serverUrl: String, accessToken: String, operationId: String): ApiResult<WebsitePublicationOperation> {
                operationIds += operationId
                if (failPublicationStorage) publicationStorage.failWrites = true
                failure?.let { throw it }
                return ApiResult.Success(if (keepRunning || repeatedState && operationIds.size < 4) operation.copy(state = "running", stage = "verifying") else operation)
            }
            override suspend fun websitePublicationHistory(serverUrl: String, accessToken: String, applicationId: String): ApiResult<List<WebsitePublicationOperation>> {
                historyReads++
                historyFailure?.let { throw it }
                return ApiResult.Success(history)
            }
            override suspend fun deploymentApplications(serverUrl: String, accessToken: String): ApiResult<List<DeploymentApplication>> = ApiResult.Success(applications)
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://websites-review.invalid"), "review", "test".toCharArray()) {} }
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        val index = OperationIndex(object : OperationIndexStorage {
            private var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        publicationStorage = Storage()
        publicationJournal = WebsitePublicationJournal(publicationStorage)
        if (seedPending) publicationJournal.begin(session.state.value as SessionState.Active,
            WebsitePublishRequest(operation.applicationId, operation.webServerId, operation.domain, confirmed = true))
        rule.runOnUiThread {
            model = WebsitesViewModel(session, WebPublishingRepository(gateway, session, elevation, index),
                DeploymentRepository(gateway, session), CertificateRepository(gateway, session, index, CertificateRequestJournal(Storage()), elevation), ElevationAnswerProvider.Declines, publicationJournal)

            store.put("websites", model)
        }
        rule.setContent { MaterialTheme { androidx.compose.foundation.layout.Column(
            androidx.compose.ui.Modifier.verticalScroll(androidx.compose.foundation.rememberScrollState())) {
            WorkspaceSection(visible.value) {
                if (recordsPage) WebsitePublicationRecords(model.state, model, active = visible.value)
                else PublicationResult(model.state, model, active = visible.value)
            }
            WorkspaceSection(!visible.value) { Text("Other website section") }
        } } }
        rule.runOnIdle { model.refresh() }
        rule.waitUntil(5_000) { !model.state.loading && model.state.servers is ApiResult.Success }

    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    @Test fun hiddenPublicationRecordsStopQueriesAndResumeSameTaskWhenShown() {
        recordsPage = true; keepRunning = true; show()
        rule.runOnIdle { model.refreshPublication(operation.operationId) }
        rule.waitUntil(5_000) { operationIds.size >= 2 && !model.state.publicationLoading }
        rule.runOnIdle { visible.value = false }
        rule.waitForIdle()
        rule.onNodeWithText("Other website section").assertIsDisplayed()
        val hidden = operationIds.size
        Thread.sleep(1_400)
        assertEquals(hidden, operationIds.size)
        rule.runOnIdle { visible.value = true }
        rule.waitUntil(5_000) { operationIds.size > hidden && !model.state.publicationLoading }
        rule.runOnIdle { assertTrue(operationIds.all { it == operation.operationId }) }
    }
    @Test fun backgroundStopsPublicationQueriesAndForegroundResumesOriginalTask() {
        keepRunning = true; show()
        rule.runOnIdle { model.refreshPublication(operation.operationId) }
        rule.waitUntil(5_000) { operationIds.size >= 2 && !model.state.publicationLoading }
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitForIdle(); rule.waitUntil(5_000) { !model.state.publicationLoading }
        val paused = operationIds.size
        Thread.sleep(1_400)
        assertEquals(paused, operationIds.size)
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(5_000) { operationIds.size > paused && !model.state.publicationLoading }
        rule.runOnIdle { assertTrue(operationIds.all { it == operation.operationId }) }
    }
    @Test fun cancelledLookupCanRetryOriginalOperationWithoutPublishing() {
        show()
        rule.runOnIdle { model.refreshPublication(operation.operationId) }
        rule.waitUntil(5_000) { model.state.publication is ApiResult.Success && !model.state.publicationLoading }
        rule.runOnIdle { failure = CancellationException("private lookup failure") }
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { model.state.publication is ApiResult.Transport && !model.state.publicationLoading }
        rule.onNodeWithText(text(R.string.websites_load_failed)).assertIsDisplayed()
        rule.onNodeWithText("private lookup failure", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_refresh)).assertIsEnabled()
        rule.runOnIdle { failure = null }
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { model.state.publication is ApiResult.Success && !model.state.publicationLoading }
        rule.runOnIdle { assertEquals(List(3) { operation.operationId }, operationIds) }
    }
    @Test fun identicalNonterminalRepliesContinuePollingUntilTerminal() {
        show()
        rule.runOnIdle { repeatedState = true; model.refreshPublication(operation.operationId) }
        rule.waitUntil(8_000) { (model.state.publication as? ApiResult.Success)?.value?.state == "succeeded" }
        rule.runOnIdle { assertEquals(List(4) { operation.operationId }, operationIds) }
    }
    @Test fun prerequisiteRefreshRemovesRecordsForUnavailableApplication() {
        show()
        rule.runOnIdle { model.selectApplication("application-a"); model.refreshPublication(operation.operationId) }
        rule.waitUntil(5_000) { !model.state.historyLoading && model.state.publication is ApiResult.Success }
        val history = context.getString(R.string.websites_operation_history, operation.domain, operation.state, operation.stage)
        rule.onNodeWithText(history).assertIsDisplayed()
        rule.runOnIdle { model.refresh() }
        rule.waitUntil(5_000) { !model.state.loading && model.state.selectedApplicationId == null }
        rule.onNodeWithText(history).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_refresh)).assertDoesNotExist()
        rule.runOnIdle { assertNull(model.state.publication); assertNull(model.state.publicationLookupId) }
    }
    @Test fun failedHistoryCanRetryWithoutPublishing() = historyCanRecover(IllegalStateException("private history failure"))
    @Test fun noTargetAndAuthoritativeEmptyHistoryHaveDistinctFeedback() {
        show()
        rule.onNodeWithText(text(R.string.websites_records_no_target)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.websites_history_empty)).assertDoesNotExist()
        rule.runOnIdle { history = emptyList(); model.selectApplication("application-a") }
        rule.waitUntil(5_000) { !model.state.historyLoading && model.state.publicationHistory is ApiResult.Success }
        rule.onNodeWithText(text(R.string.websites_history_empty)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.websites_records_no_target)).assertDoesNotExist()
    }
    @Test fun fourthReturnedHistoryRecordRemainsAccessible() {
        show()
        rule.runOnIdle { history = (1..4).map { operation.copy(domain = "site-$it.example.test") }; model.selectApplication("application-a") }
        rule.waitUntil(5_000) { !model.state.historyLoading && model.state.publicationHistory is ApiResult.Success }
        rule.onNodeWithText(context.getString(R.string.websites_operation_history, "site-4.example.test", operation.state, operation.stage)).performScrollTo().assertIsDisplayed()
    }
    @Test fun stoppedApplicationHistoryCanBeSelectedWithoutWebServer() {
        recordsPage = true
        applications = listOf(stoppedApplication.copy(id = "application-a", name = "Other stopped application"), stoppedApplication)
        history = listOf(operation.copy(applicationId = "application-b"))
        show()
        rule.onNodeWithText(stoppedApplication.name).performClick()
        rule.waitUntil(5_000) { !model.state.historyLoading && model.state.selectedApplicationId == "application-b" }
        rule.onNodeWithText(context.getString(R.string.websites_operation_history, operation.domain, operation.state, operation.stage)).assertIsDisplayed()
        rule.runOnIdle {
            assertTrue((model.state.servers as ApiResult.Success).value.isEmpty())
            model.publish("example.test", "review@example.test", null, true, true)
            assertFalse(model.state.publishing)
            assertTrue(operationIds.isEmpty())
        }
    }
    @Test fun cancelledHistoryCanRetryWithoutPublishing() = historyCanRecover(CancellationException("private history failure"))
    private fun historyCanRecover(error: Exception) {
        show()
        rule.runOnIdle { historyFailure = error; model.selectApplication("application-a") }
        rule.waitUntil(5_000) { !model.state.historyLoading && model.state.publicationHistory is ApiResult.Transport }
        rule.onNodeWithText(text(R.string.websites_history_failed)).assertIsDisplayed()
        rule.onNodeWithText("private history failure", substring = true).assertDoesNotExist()
        rule.runOnIdle { historyFailure = null }
        rule.onNodeWithText(text(R.string.common_retry)).performClick()
        rule.waitUntil(5_000) { !model.state.historyLoading && model.state.publicationHistory is ApiResult.Success }
        rule.onNodeWithText(context.getString(R.string.websites_operation_history, operation.domain, operation.state, operation.stage)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(2, historyReads); assertTrue(operationIds.isEmpty()) }
    }
    @Test fun pendingPublicationCanBeVerifiedByOriginalIdWithoutPublishing() {
        seedPending = true
        show()
        rule.runOnIdle { model.refresh() }
        rule.waitUntil(5_000) { model.state.pendingPublication != null }
        rule.onNodeWithText(text(R.string.websites_pending_publication)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.websites_original_operation)).performTextInput(operation.operationId)
        rule.onAllNodesWithText(text(R.string.common_refresh)).onFirst().performClick()
        rule.waitUntil(5_000) { !model.state.publishing && model.state.pendingPublication == null }
        assertEquals(listOf(operation.operationId), operationIds)
        rule.onNodeWithText(text(R.string.websites_pending_publication)).assertDoesNotExist()
    }

    @Test fun historyEntryOpensOriginalOperationWithSelectableId() {
        show()
        rule.runOnIdle { model.selectApplication(operation.applicationId) }
        rule.waitUntil(5_000) { !model.state.historyLoading && model.state.publicationHistory is ApiResult.Success }
        rule.onNodeWithText(context.getString(R.string.websites_operation_history, operation.domain, operation.state, operation.stage))
            .performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.publicationLoading && model.state.publication is ApiResult.Success }
        rule.onNodeWithText(context.getString(R.string.websites_operation_id, operation.operationId)).performScrollTo().assertIsDisplayed()
        assertEquals(listOf(operation.operationId), operationIds)
    }
    @Test fun localStorageFailureKeepsReceiptVisibleAndAllowsVerificationRetry() {
        seedPending = true
        failPublicationStorage = true
        show()
        rule.onNodeWithText(text(R.string.websites_original_operation)).performTextInput(operation.operationId)
        rule.onAllNodesWithText(text(R.string.common_refresh)).onFirst().performClick()
        rule.waitUntil(5_000) { !model.state.publishing && model.state.publication is ApiResult.Success }
        rule.onNodeWithText(text(R.string.websites_publication_storage_failed)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.websites_operation_id, operation.operationId)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.websites_publish_transport_failed)).assertDoesNotExist()
        rule.runOnIdle { failPublicationStorage = false; publicationStorage.failWrites = false }
        rule.onAllNodesWithText(text(R.string.common_refresh)).onFirst().performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.publishing && model.state.pendingPublication == null }
        rule.onNodeWithText(text(R.string.websites_publication_storage_failed)).assertDoesNotExist()
        assertEquals(listOf(operation.operationId, operation.operationId), operationIds)
    }

}
