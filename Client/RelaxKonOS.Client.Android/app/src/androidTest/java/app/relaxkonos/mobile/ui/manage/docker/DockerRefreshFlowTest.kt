package app.relaxkonos.mobile.ui.manage.docker

import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.ViewModelStore
import androidx.test.espresso.Espresso
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import kotlinx.coroutines.*
import org.junit.*
import org.junit.Assert.*
import java.io.File
import java.util.UUID

class DockerRefreshFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: DockerViewModel
    private var fail = false
    private var cancel = false
    private var reads = 0
    private var previewAttempts = 0
    private var cancelPreview = false
    private var failPreview = false
    private var deployments = 0
    private var allowSubmission = false
    private var cancelSubmission = false
    private var lookupFound = false
    private var submittedKey: String? = null
    private val lookupKeys = mutableListOf<String>()
    private lateinit var journalDirectory: File
    private fun memoryStorage() = object : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes }
    }
    private fun text(id: Int) = rule.activity.getString(id)
    private fun show() {
        val app = rule.activity.application as RelaxKonApplication
        journalDirectory = File(app.cacheDir, "compose-recovery-" + UUID.randomUUID())
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun dockerStackDeploy(serverUrl: String, accessToken: String, name: String, composeYaml: String, definitionVersion: String, idempotencyKey: String): ApiResult<DockerStackOperation> {
                deployments++; submittedKey = idempotencyKey
                check(allowSubmission)
                if (cancelSubmission) throw CancellationException("private lost receipt")
                return ApiResult.Transport(null)
            }
            override suspend fun dockerStackOperationByRequest(serverUrl: String, accessToken: String, requestKey: String): ApiResult<DockerStackOperation> {
                lookupKeys += requestKey
                return if (!lookupFound) ApiResult.Problem(404, "", null) else ApiResult.Success(DockerStackOperation(
                    "11111111-1111-1111-1111-111111111111", "unsaved-project", DockerStackOperationKind.DEPLOY,
                    DockerStackOperationState.SUCCEEDED, "completed", null, null, "2026-10-14T00:00:00Z", false, emptyList()))
            }
            override suspend fun dockerStackPreview(serverUrl: String, accessToken: String, name: String, composeYaml: String): ApiResult<DockerStackPreview> {
                previewAttempts++
                if (cancelPreview) throw CancellationException("private preview cancellation")
                if (failPreview) error("private preview diagnostic")
                if (allowSubmission) return ApiResult.Success(DockerStackPreview(name, "version", emptyList(), emptyList(), emptyList()))
                return ApiResult.Problem(400, "docker.stack.invalid_yaml", null)
            }
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.DOCKER), true),
                    AuthTokens("test", "test", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun dockerStatus(serverUrl: String, accessToken: String): ApiResult<DockerStatus> {
                reads++
                if (cancel) throw CancellationException("private cancelled read")
                if (fail) error("private provider exception")
                return ApiResult.Success(DockerStatus(true, "", "test", "linux", "amd64"))
            }
            override suspend fun dockerContainers(serverUrl: String, accessToken: String) = ApiResult.Success(emptyList<DockerContainer>())
            override suspend fun dockerImages(serverUrl: String, accessToken: String) = ApiResult.Success(emptyList<DockerImage>())
            override suspend fun dockerNetworks(serverUrl: String, accessToken: String) = ApiResult.Success(emptyList<DockerNetwork>())
            override suspend fun dockerVolumes(serverUrl: String, accessToken: String) = ApiResult.Success(emptyList<DockerVolume>())
            override suspend fun dockerStacks(serverUrl: String, accessToken: String) = ApiResult.Success(listOf(DockerStack("review-stack", "running", "", "")))
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://docker-review.invalid"), "review", "test".toCharArray()) {} }
        val repository = DockerRepository(gateway, session, OperationIndex(object : OperationIndexStorage {
            override fun read(): ByteArray? = null
            override fun write(bytes: ByteArray) {}
        }), DockerMutationGate(DockerControlJournal(memoryStorage()), DockerResourceJournal(memoryStorage()),
            DockerComposeJournal(FileDockerComposeStorage(journalDirectory))) { ApiResult.Success(Unit) })
        rule.runOnUiThread { model = DockerViewModel(session, repository) { null }; store.put("docker", model) }
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides app.container) { MaterialTheme {
            DockerContent(model, null, true, section = "compose")
        } } }
        rule.waitUntil(5_000) { !model.state.loading && model.state.stacks != null }
        rule.onNodeWithText("review-stack").assertExists()
    }
    @After fun cleanup() {
        rule.runOnUiThread { store.clear() }
        if (::journalDirectory.isInitialized) {
            File(journalDirectory, "docker-compose-requests.bin").delete()
            File(journalDirectory, "docker-compose-requests.bin.tmp").delete()
            journalDirectory.delete()
        }
    }
    private fun refresh() = rule.onNodeWithText(text(R.string.common_refresh)).performClick()
    private fun dismissFailure() {
        rule.onNodeWithText(text(R.string.error_connectivity)).assertIsDisplayed()
        rule.onNodeWithText("private", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
    }
    private fun verifyPreviewRecovery(cancelled: Boolean, deploying: Boolean = false) {
        show()
        rule.onNodeWithText(text(R.string.docker_new_stack)).performClick()
        rule.onNodeWithText(text(R.string.docker_stack_name)).performTextInput("unsaved-project")
        Espresso.closeSoftKeyboard()
        rule.runOnIdle { cancelPreview = cancelled; failPreview = !cancelled }
        rule.onNodeWithText(text(if (deploying) R.string.docker_deploy else R.string.docker_preview)).performClick()
        rule.waitUntil(5_000) { previewAttempts == 1 && !model.state.busy }
        rule.onNodeWithText(text(if (cancelled) R.string.error_connectivity else R.string.error_generic)).assertIsDisplayed()
        rule.onNodeWithText("private", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText("unsaved-project").assertExists().assertIsEnabled()
        rule.onNodeWithText(text(R.string.docker_preview)).assertIsEnabled()
        rule.runOnIdle { cancelPreview = false; failPreview = false }
        rule.onNodeWithText(text(R.string.docker_preview)).performClick()
        rule.waitUntil(5_000) { previewAttempts == 2 && !model.state.busy }
        rule.runOnIdle { assertNull(model.state.preview); assertNotNull(model.state.message); assertEquals(0, deployments) }
    }
    @Test fun cancelledPreviewKeepsDraftAndRestoresEditorActions() = verifyPreviewRecovery(true)
    @Test fun exceptionInPreviewKeepsDraftAndRestoresEditorActions() = verifyPreviewRecovery(false)
    @Test fun cancelledDeploymentPreflightKeepsDraftWithoutSubmission() = verifyPreviewRecovery(true, true)
    private fun verifyReceiptRecovery(cancelled: Boolean) {
        show()
        rule.runOnIdle { allowSubmission = true; cancelSubmission = cancelled }
        rule.onNodeWithText(text(R.string.docker_new_stack)).performClick()
        rule.onNodeWithText(text(R.string.docker_stack_name)).performTextInput("unsaved-project")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.docker_deploy)).performClick()
        rule.waitUntil(5_000) { deployments == 1 && !model.state.busy }
        rule.onNode(hasText(text(R.string.docker_stack_result_unknown)) and
            hasAnyAncestor(isDialog() and hasAnyDescendant(hasText(text(R.string.common_dismiss))))).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText("unsaved-project").assertExists()
        rule.onNodeWithText(text(R.string.docker_deploy)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.docker_stack_verify_request)).performScrollTo().performClick()
        rule.waitUntil(5_000) { lookupKeys.size == 1 && !model.state.busy }
        rule.onNodeWithText(text(R.string.docker_stack_lookup_unresolved)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.docker_deploy)).assertIsNotEnabled()
        rule.runOnIdle { lookupFound = true }
        rule.onNodeWithText(text(R.string.docker_stack_verify_request)).performScrollTo().performClick()
        rule.waitUntil(5_000) { lookupKeys.size == 2 && !model.state.busy }
        rule.runOnIdle {
            assertTrue(model.state.pendingRequests.isEmpty())
            assertEquals(listOf(submittedKey, submittedKey), lookupKeys)
            assertEquals(1, deployments)
            assertEquals(DockerStackOperationState.SUCCEEDED, (model.state.operations as ApiResult.Success).value.single().state)
        }
    }
    @Test fun transportReceiptLossAllowsOnlyExactReadbackAndPreservesDraft() = verifyReceiptRecovery(false)
    @Test fun cancelledSubmissionAllowsOnlyExactReadbackAndPreservesDraft() = verifyReceiptRecovery(true)
    @Test fun cancelledRefreshShowsFailureInsteadOfPermanentLoadingAndCanRecover() {
        show(); rule.runOnIdle { cancel = true }; refresh()
        rule.waitUntil(5_000) { reads == 2 && !model.state.loading }
        dismissFailure()
        rule.onNodeWithText(text(R.string.docker_list_failed)).assertIsDisplayed()
        rule.onNodeWithText("review-stack").assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_refresh)).assertIsEnabled()
        rule.runOnIdle { cancel = false }; refresh()
        rule.waitUntil(5_000) { reads == 3 && !model.state.loading }
        rule.onNodeWithText("review-stack").assertIsDisplayed()
    }
    @Test fun failedRuntimeReadKeepsSuccessfulStackListAndExplicitRetryRestoresStatus() {
        show(); rule.runOnIdle { fail = true }; refresh()
        rule.waitUntil(5_000) { reads == 2 && !model.state.loading }
        dismissFailure()
        rule.onNodeWithText("review-stack").assertIsDisplayed()
        rule.runOnIdle { fail = false }; refresh()
        rule.waitUntil(5_000) { reads == 3 && !model.state.loading }
        rule.onNodeWithText("review-stack").assertIsDisplayed()
        rule.runOnIdle { assertTrue(model.state.status is ApiResult.Success) }
    }
}
