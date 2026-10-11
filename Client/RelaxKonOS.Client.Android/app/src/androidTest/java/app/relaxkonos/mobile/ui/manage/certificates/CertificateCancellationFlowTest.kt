package app.relaxkonos.mobile.ui.manage.certificates

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

class CertificateCancellationFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }

    @Test fun cancelledSubmissionShowsUnknownAndPreservesLockedOriginalDraft() {
        val app = context.applicationContext as RelaxKonApplication
        var writes = 0
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.CERTIFICATES), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun certificates(serverUrl: String, accessToken: String): ApiResult<List<ManagedCertificate>> = ApiResult.Success(emptyList())
            override suspend fun certificateMutation(serverUrl: String, accessToken: String, action: CertificateAction,
                id: String?, body: JsonBody, idempotencyKey: String): ApiResult<CertificateOperation> {
                writes++; throw CancellationException("private gateway detail")
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://certificate-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val index = OperationIndex(object : OperationIndexStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        val journal = CertificateRequestJournal(object : InstallationRequestStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        val repository = CertificateRepository(gateway, session, index, journal,
            ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage())))
        lateinit var model: CertificatesViewModel
        rule.runOnUiThread {
            model = CertificatesViewModel(session, repository, index, ElevationAnswerProvider.Declines)
            store.put("certificates", model)
        }
        rule.setContent { MaterialTheme { CertificatesContent(model, owner, {}) } }
        rule.waitUntil(5_000) { model.state.list is ApiResult.Success && !model.state.busy }
        rule.onNodeWithText(text(R.string.certificates_self_signed)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_domains)).performScrollTo().performTextInput("host.example.test")
        rule.onNodeWithText(text(R.string.certificates_submit)).performClick()
        rule.onAllNodesWithText(text(R.string.certificates_confirm)).filter(hasClickAction()).onFirst().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.uncertain }
        rule.onNodeWithText(text(R.string.certificates_uncertain)).assertIsDisplayed()
        rule.onNodeWithText("private gateway detail", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.certificates_domains)).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText("host.example.test").assertExists()
        rule.runOnIdle {
            assertEquals(1, writes)
            assertTrue(model.state.draftLocked)
            assertEquals("host.example.test", model.state.draft!!.domainsText)
            assertTrue(model.state.pending.single().attempted)
            assertFalse(model.state.operationVerified)
        }
    }
}
