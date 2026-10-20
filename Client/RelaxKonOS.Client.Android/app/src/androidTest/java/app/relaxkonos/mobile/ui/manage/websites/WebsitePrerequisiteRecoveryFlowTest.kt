package app.relaxkonos.mobile.ui.manage.websites

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
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.*
import org.junit.Assert.*

class WebsitePrerequisiteRecoveryFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: WebsitesViewModel
    private var failure: Exception? = null
    private var reads = 0
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.WEB_SERVER, ServerCapabilities.APPLICATION_DEPLOYMENTS), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun webServers(serverUrl: String, accessToken: String): ApiResult<List<WebServer>> {
                reads++; failure?.let { throw it }
                return ApiResult.Success(emptyList())
            }
            override suspend fun deploymentApplications(serverUrl: String, accessToken: String): ApiResult<List<DeploymentApplication>> = ApiResult.Success(emptyList())
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://websites-review.invalid"), "review", "test".toCharArray()) {} }
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        val index = OperationIndex(object : OperationIndexStorage {
            private var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        rule.runOnUiThread {
            model = WebsitesViewModel(session, WebPublishingRepository(gateway, session, elevation, index),
                DeploymentRepository(gateway, session), CertificateRepository(gateway, session, index, CertificateRequestJournal(Storage()), elevation), ElevationAnswerProvider.Declines, WebsitePublicationJournal(Storage()))

            store.put("websites", model)
        }
        rule.setContent { MaterialTheme { WebsitePublisher(model.state, model, {}) } }
        rule.runOnIdle { model.refresh() }
        rule.waitUntil(5_000) { !model.state.loading && model.state.servers is ApiResult.Success }
        rule.onNodeWithText(text(R.string.websites_publish_no_eligible_server)).assertIsDisplayed()
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun verifyRecovery(exception: Exception) {
        show()
        rule.runOnIdle { failure = exception }
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { !model.state.loading && model.state.servers is ApiResult.Transport }
        rule.onNodeWithText(text(R.string.websites_load_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.websites_publish_no_eligible_server)).assertDoesNotExist()
        rule.onNodeWithText("private read failure", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_refresh)).assertIsEnabled()
        rule.runOnIdle { failure = null }
        rule.onNodeWithText(text(R.string.common_refresh)).performClick()
        rule.waitUntil(5_000) { !model.state.loading && model.state.servers is ApiResult.Success }
        rule.onNodeWithText(text(R.string.websites_publish_no_eligible_server)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(3, reads) }
    }
    @Test fun cancelledReadOffersExplicitRecovery() = verifyRecovery(CancellationException("private read failure"))
    @Test fun failedReadOffersExplicitRecoveryWithoutExceptionDetails() = verifyRecovery(IllegalStateException("private read failure"))
}
