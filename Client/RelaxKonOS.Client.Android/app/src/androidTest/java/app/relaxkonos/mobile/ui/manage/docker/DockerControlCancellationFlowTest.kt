package app.relaxkonos.mobile.ui.manage.docker

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

class DockerControlCancellationFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: DockerControlViewModel
    private var cancelRead = false
    private var writes = 0
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val facts = DockerControlFacts(DockerStatus(true, "", "28", "linux", "amd64"), emptyList())
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.DOCKER), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun dockerStatus(serverUrl: String, accessToken: String): ApiResult<DockerStatus> {
                if (cancelRead) throw CancellationException("private cancelled read")
                return ApiResult.Success(facts.status)
            }
            override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService): ApiResult<InstallationOperation?> = ApiResult.Success(null)
            override suspend fun dockerMirrors(serverUrl: String, accessToken: String): ApiResult<List<DockerImageMirror>> = ApiResult.Success(emptyList())
            override suspend fun dockerEngineAction(serverUrl: String, accessToken: String, action: DockerEngineAction, confirmed: Boolean): ApiResult<DockerEngineResult> {
                writes++; throw CancellationException("private cancelled write")
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://docker-control-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        val controlJournal = DockerControlJournal(Storage())
        val resourceJournal = DockerResourceJournal(Storage())
        val gate = DockerMutationGate(controlJournal, resourceJournal) { ApiResult.Success(Unit) }
        val index = OperationIndex(object : OperationIndexStorage {
            private var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        rule.runOnUiThread {
            model = DockerControlViewModel(session, DockerControlRepository(gateway, session, controlJournal, gate),
                DockerResourceRepository(gateway, session, resourceJournal, gate),
                InstallationRepository(gateway, session, elevation, index, InstallationRequestJournal(Storage())), index, gate, ElevationAnswerProvider.Declines)
            store.put("firewall", model)
        }
        rule.setContent { MaterialTheme { DockerControlContent(model, owner, {}) } }
        rule.waitUntil(5_000) { model.state.facts != null && !model.state.busy }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun refresh() = rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
    private fun dismissFailure() {
        rule.onNodeWithText(text(R.string.common_dismiss)).assertIsDisplayed().performClick()
        rule.onNodeWithText(text(R.string.docker_control_unverified)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).assertDoesNotExist()
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
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun cancelledChangeRetainsMarkerAndRefreshDoesNotReplayWrite() {
        show()
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).performScrollTo().performClick()
        rule.onNode(hasClickAction() and hasText(text(R.string.docker_control_confirm))).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isNotEmpty() }
        dismissFailure()
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.docker_control_engine_restart)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, writes); assertEquals(1, model.state.pending.size) }
    }
}

