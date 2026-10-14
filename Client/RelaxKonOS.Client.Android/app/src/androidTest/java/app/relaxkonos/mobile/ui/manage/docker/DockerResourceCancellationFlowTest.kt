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
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class DockerResourceCancellationFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: DockerResourceViewModel
    private var cancelRead = false
    private var writes = 0
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
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.DOCKER), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun dockerStatus(serverUrl: String, accessToken: String): ApiResult<DockerStatus> {
                if (cancelRead) throw CancellationException("private cancelled read")
                return ApiResult.Success(DockerStatus(true, "", "28", "linux", "amd64"))
            }
            override suspend fun dockerContainers(serverUrl: String, accessToken: String): ApiResult<List<DockerContainer>> = ApiResult.Success(emptyList())
            override suspend fun dockerImages(serverUrl: String, accessToken: String): ApiResult<List<DockerImage>> = ApiResult.Success(emptyList())
            override suspend fun dockerNetworks(serverUrl: String, accessToken: String): ApiResult<List<DockerNetwork>> = ApiResult.Success(emptyList())
            override suspend fun dockerVolumes(serverUrl: String, accessToken: String): ApiResult<List<DockerVolume>> = ApiResult.Success(emptyList())
            override suspend fun dockerResourceChange(serverUrl: String, accessToken: String, change: DockerResourceChange): ApiResult<DockerOperation> {
                writes++; throw CancellationException("private cancelled write")
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://docker-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val journal = DockerResourceJournal(Storage())
        val gate = DockerMutationGate(DockerControlJournal(Storage()), journal, DockerComposeJournal(Storage())) { ApiResult.Success(Unit) }
        rule.runOnUiThread {
            model = DockerResourceViewModel(session, DockerResourceRepository(gateway, session, journal, gate), gate)
            store.put("resources", model)
        }
        rule.setContent { MaterialTheme { DockerResourceContent(model, owner, {}, {}, {}, {}, resourceKind = DockerResourceKind.Volumes) } }
        rule.waitUntil(5_000) { model.state.facts != null && !model.state.busy }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun refresh() = rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
    private fun dismissFailure() {
        rule.onNodeWithText(text(R.string.common_dismiss)).assertIsDisplayed().performClick()
        rule.onNodeWithText(text(R.string.docker_control_unverified)).assertIsDisplayed()
        rule.onNode(hasClickAction() and hasText(text(R.string.docker_resources_create_volume))).assertDoesNotExist()
    }
    @Test fun cancelledRefreshRemovesActionsUntilExplicitRecovery() {
        show()
        rule.runOnIdle { cancelRead = true }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts == null }
        dismissFailure()
        rule.runOnIdle { cancelRead = false }
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.docker_resources_create_volume)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun cancelledCreationKeepsMarkerAndRefreshDoesNotReplay() {
        show()
        rule.onNodeWithText(text(R.string.docker_resources_create_volume)).performScrollTo().performClick()
        rule.onNode(hasSetTextAction() and hasText(text(R.string.docker_resources_name))).performScrollTo().performTextInput("review-data")
        rule.onNodeWithText(text(R.string.docker_resources_review)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.docker_resources_apply)).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isNotEmpty() }
        dismissFailure()
        refresh()
        rule.onNode(hasClickAction() and hasText(text(R.string.docker_resources_discard))).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.docker_resources_create_volume)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, writes); assertEquals(1, model.state.pending.size) }
    }
}
