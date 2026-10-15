package app.relaxkonos.mobile.ui.manage.certificates

import androidx.activity.ComponentActivity
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.Lifecycle
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.material3.Text
import app.relaxkonos.mobile.ui.common.WorkspaceSection
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
    private lateinit var model: CertificatesViewModel
    private lateinit var owner: SessionState.Active
    private lateinit var session: AuthSession
    private var writes = 0
    private var failList = false
    private val visible = mutableStateOf(true)
    private var identicalPolling = false
    private val operationReads = java.util.concurrent.atomic.AtomicInteger()
    private val tasks = mutableMapOf<String, CertificateOperation>()
    private val cancellations = mutableListOf<String>()
    private val receipt = CertificateOperation("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222",
        CertificateAction.SelfSigned, CertificateOperationState.Succeeded, "completed", "", 1, 2)
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }

    private fun show(accepted: Boolean = false, certificate: ManagedCertificate? = null, serverHttpsOnly: Boolean = false) {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.CERTIFICATES), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun certificates(serverUrl: String, accessToken: String): ApiResult<List<ManagedCertificate>> {
                if (failList) throw IllegalStateException("private list detail")
                return ApiResult.Success(listOfNotNull(certificate))
            }
            override suspend fun certificate(serverUrl: String, accessToken: String, id: String): ApiResult<ManagedCertificate> =
                certificate?.let { ApiResult.Success(it) } ?: ApiResult.Transport(null)
            override suspend fun kestrelCertificateDeployment(serverUrl: String, accessToken: String, id: String): ApiResult<KestrelCertificateDeployment> = ApiResult.Transport(null)
            override suspend fun certificateOperation(serverUrl: String, accessToken: String, id: String): ApiResult<CertificateOperation> {
                if (identicalPolling) return ApiResult.Success(receipt.copy(state =
                    if (operationReads.incrementAndGet() < 5) CertificateOperationState.Running else CertificateOperationState.Succeeded))
                return tasks[id]?.let { ApiResult.Success(it) } ?: ApiResult.Transport(null)
            }
            override suspend fun cancelCertificateOperation(serverUrl: String, accessToken: String, id: String): ApiResult<CertificateOperation> {
                cancellations += id
                return tasks[id]?.let { ApiResult.Success(it.copy(state = CertificateOperationState.Cancelled)) } ?: ApiResult.Transport(null)
            }
            override suspend fun certificateMutation(serverUrl: String, accessToken: String, action: CertificateAction,
                id: String?, body: JsonBody, idempotencyKey: String): ApiResult<CertificateOperation> {
                writes++
                if (accepted) { failList = true; return ApiResult.Success(receipt) }
                throw CancellationException("private gateway detail")
            }
        }
        session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://certificate-review.invalid"), "review", "test".toCharArray()) {} }
        owner = session.state.value as SessionState.Active
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
        rule.runOnUiThread {
            model = CertificatesViewModel(session, repository, index, ElevationAnswerProvider.Declines)
            store.put("certificates", model)
        }
        rule.setContent {
            val active by session.state.collectAsState()
            MaterialTheme {
                WorkspaceSection(visible.value) { CertificatesContent(model, active as? SessionState.Active, {}, visible.value, serverHttpsOnly = serverHttpsOnly) }
                WorkspaceSection(!visible.value) { Text("Other settings") }
            }
        }
        rule.waitUntil(5_000) { model.state.list is ApiResult.Success && !model.state.busy }
    }
    private fun submitSelfSigned() {
        rule.onNodeWithText(text(R.string.certificates_self_signed)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_domains)).performScrollTo().performTextInput("host.example.test")
        rule.onNodeWithText(text(R.string.certificates_submit)).performClick()
        rule.onAllNodesWithText(text(R.string.certificates_confirm)).filter(hasClickAction()).onFirst().performClick()
    }
    @Test fun hiddenEditorWithdrawsConfirmationAndPreservesDraftWithoutMutation() {
        show()
        rule.onNodeWithText(text(R.string.certificates_self_signed)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_domains)).performScrollTo().performTextInput("host.example.test")
        rule.onNodeWithText(text(R.string.certificates_submit)).performClick()
        rule.onNodeWithText(text(R.string.certificates_submit_confirm)).assertIsDisplayed()
        rule.runOnIdle { visible.value = false }
        rule.onNodeWithText("Other settings").assertIsDisplayed()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { visible.value = true }
        rule.onNodeWithText("host.example.test").assertExists()
        rule.onNodeWithText(text(R.string.certificates_submit_confirm)).assertDoesNotExist()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun hiddenPageStopsObservationAndVisiblePageResumesOriginalOperation() {
        show(); identicalPolling = true
        rule.runOnIdle { model.recover(receipt.operationId) }
        rule.waitUntil(5_000) { operationReads.get() == 1 && !model.state.busy }
        rule.runOnIdle { visible.value = false }
        rule.onNodeWithText("Other settings").assertIsDisplayed()
        val before = operationReads.get()
        rule.mainClock.advanceTimeBy(5_000)
        rule.waitForIdle()
        assertEquals(before, operationReads.get())
        rule.runOnIdle { visible.value = true }
        rule.waitUntil(10_000) { model.state.operation?.state == CertificateOperationState.Succeeded }
        rule.runOnIdle { assertEquals(0, writes); assertTrue(cancellations.isEmpty()) }
    }
    @Test fun serverHttpsEmptyStateExplainsCertificateCreationAndWithdrawsHintOnReadFailure() {
        show(serverHttpsOnly = true)
        val guidance = context.getString(R.string.server_https_empty_guidance, text(R.string.nav_manage), text(R.string.certificates_title))
        rule.onNodeWithText(guidance).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.certificates_self_signed)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.certificates_issue)).assertDoesNotExist()
        rule.runOnIdle { failList = true }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.problemCode == "certificate.read_failed" }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(guidance).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.certificates_unavailable)).assertIsDisplayed()
        rule.runOnIdle { failList = false }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.list is ApiResult.Success }
        rule.onNodeWithText(guidance).assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun emptyCertificateListDoesNotAskForUnavailableSelectionAndRefreshDistinguishesFailure() {
        show()
        rule.onAllNodesWithText(text(R.string.workspace_certificates)).filter(hasClickAction()).onFirst().performClick()
        rule.onNodeWithText(text(R.string.certificates_empty)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.certificates_select)).assertDoesNotExist()
        rule.runOnIdle { failList = true }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.problemCode == "certificate.read_failed" }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.certificates_empty)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.certificates_select)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.certificates_unavailable)).assertIsDisplayed()
        rule.runOnIdle { failList = false }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.list is ApiResult.Success }
        rule.onNodeWithText(text(R.string.certificates_empty)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    private fun pauseAndResumeWithoutComposeFrames() {
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
    }
    @Test fun pausedDeleteConfirmationIsWithdrawnAndFreshActionStillAvailable() {
        val certificate = ManagedCertificate(requireNotNull(receipt.certificateId), "host.example.test", listOf("host.example.test"),
            CertificateStatus.Issued, CertificateChallenge.DirectHttp01, 4102444800000L, null, null, null, 0,
            CertificateKey.EcdsaP256, null, null, null, null, 0, 0, CertificateKind.SelfSigned, "review-fingerprint")
        show(certificate = certificate)
        rule.onAllNodesWithText(text(R.string.workspace_certificates)).filter(hasClickAction()).onFirst().performClick()
        rule.runOnIdle { model.select(certificate.id) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.detail is ApiResult.Success }
        rule.onNodeWithText(text(R.string.common_delete)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_delete_confirm)).assertIsDisplayed()
        pauseAndResumeWithoutComposeFrames()
        rule.onNodeWithText(text(R.string.certificates_delete_confirm)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_delete)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_delete_confirm)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    private fun verifyEditorConfirmationWithdrawal(submitting: Boolean) {
        show()
        rule.onNodeWithText(text(R.string.certificates_self_signed)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_domains)).performScrollTo().performTextInput("host.example.test")
        val action = if (submitting) R.string.certificates_submit else R.string.common_close
        val message = if (submitting) R.string.certificates_submit_confirm else R.string.certificates_discard_confirm
        rule.onNodeWithText(text(action)).performClick()
        rule.onNodeWithText(text(message)).assertIsDisplayed()
        pauseAndResumeWithoutComposeFrames()
        rule.onNodeWithText(text(message)).assertDoesNotExist()
        rule.onNodeWithText("host.example.test").assertExists()
        rule.onNodeWithText(text(action)).performClick()
        rule.onNodeWithText(text(message)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertEquals(0, writes); assertEquals("host.example.test", model.state.draft?.domainsText) }
    }
    @Test fun pausedSubmissionConfirmationPreservesDraftAndRequiresFreshConfirmation() = verifyEditorConfirmationWithdrawal(true)
    @Test fun pausedDiscardConfirmationPreservesDraftAndRequiresFreshConfirmation() = verifyEditorConfirmationWithdrawal(false)
    @Test fun identicalRunningResponsesReachTerminal() {
        show(); identicalPolling = true
        rule.runOnIdle { model.recover(receipt.operationId) }
        rule.waitUntil(5_000) { operationReads.get() == 1 && !model.state.busy }
        rule.waitForIdle()
        rule.waitUntil(10_000) { model.state.operation?.state == CertificateOperationState.Succeeded }
        assertEquals(5, operationReads.get())
        assertEquals(0, writes); assertTrue(cancellations.isEmpty())
    }
    private fun verifyChallengeDetails(kind: CertificateKind) {
        val certificate = ManagedCertificate(requireNotNull(receipt.certificateId), "host.example.test", listOf("host.example.test"),
            CertificateStatus.Active, CertificateChallenge.Dns01, 4102444800000L, null, null, null, 0,
            CertificateKey.EcdsaP256, 4070908800000L, 4102444800000L, null, null, 0, 0, kind, "review-fingerprint")
        show(certificate = certificate)
        rule.onAllNodesWithText(text(R.string.workspace_certificates)).filter(hasClickAction()).onFirst().performClick()
        rule.runOnIdle { model.select(certificate.id) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.detail is ApiResult.Success }
        rule.onAllNodesWithText(text(R.string.certificates_status_active)).onFirst().assertExists()
        val challenge = rule.onNodeWithText(text(R.string.certificates_challenge_dns))
        val start = rule.onNodeWithText(text(R.string.certificates_renewal_start).substringBefore('%'), substring = true)
        val end = rule.onNodeWithText(text(R.string.certificates_renewal_end).substringBefore('%'), substring = true)
        if (kind == CertificateKind.SelfSigned) {
            challenge.assertDoesNotExist(); start.assertDoesNotExist(); end.assertDoesNotExist()
            rule.onNodeWithText(text(R.string.certificates_self_signed_note)).performScrollTo().assertIsDisplayed()
        } else {
            challenge.performScrollTo().assertIsDisplayed()
            start.performScrollTo().assertIsDisplayed(); end.performScrollTo().assertIsDisplayed()
        }
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun selfSignedDetailOmitsAcmeChallengeAndRenewalWindows() = verifyChallengeDetails(CertificateKind.SelfSigned)
    @Test fun acmeDetailRetainsChallengeAndRenewalWindows() = verifyChallengeDetails(CertificateKind.Acme)
    @Test fun changedDraftWithdrawsSubmissionConfirmation() {
        show()
        rule.onAllNodesWithText(text(R.string.workspace_certificates)).filter(hasClickAction()).onFirst().performClick()
        rule.onNodeWithText(text(R.string.certificates_self_signed)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_domains)).performScrollTo().performTextInput("host.example.test")
        rule.onNodeWithText(text(R.string.certificates_submit)).performClick()
        rule.onNodeWithText(text(R.string.certificates_submit_confirm)).assertIsDisplayed()
        rule.runOnIdle { model.update(requireNotNull(model.state.draft).copy(domainsText = "edited.example.test")) }
        rule.onNodeWithText(text(R.string.certificates_submit_confirm)).assertDoesNotExist()
        rule.onNodeWithText("edited.example.test").assertExists()
        rule.runOnIdle { assertEquals(0, writes) }
        rule.onNodeWithText(text(R.string.certificates_submit)).performClick()
        rule.onAllNodesWithText(text(R.string.certificates_confirm)).filter(hasClickAction()).onFirst().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.uncertain }
        rule.runOnIdle { assertEquals(1, writes); assertEquals("edited.example.test", model.state.draft!!.domainsText) }
    }
    @Test fun cancelledSubmissionShowsUnknownAndPreservesLockedOriginalDraft() {
        show(); submitSelfSigned()
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
    @Test fun acceptedSubmissionClosesEditorAndRetainsReceiptWhenListRefreshThrows() {
        show(accepted = true); submitSelfSigned()
        rule.waitUntil(5_000) { !model.state.busy && model.state.problemCode == "certificate.read_failed" }
        rule.onNodeWithText(text(R.string.certificates_read_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.certificates_uncertain)).assertDoesNotExist()
        rule.onNodeWithText("private list detail", substring = true).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.certificates_domains)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.workspace_operations)).performClick()
        rule.onNodeWithText(receipt.operationId).performScrollTo().assertIsDisplayed()
        rule.runOnIdle {
            assertEquals(1, writes); assertNull(model.state.draft)
            assertEquals(receipt, model.state.operation); assertTrue(model.state.operationVerified)
            assertTrue(model.state.pending.isEmpty()); assertFalse(model.state.uncertain)
        }
    }
    @Test fun failedRefreshWithdrawsOldDeleteActionAndActualRefreshRestoresIt() {
        val certificate = ManagedCertificate(requireNotNull(receipt.certificateId), "host.example.test", listOf("host.example.test"),
            CertificateStatus.Issued, CertificateChallenge.DirectHttp01, 4102444800000L, null, null, null, 0,
            CertificateKey.EcdsaP256, null, null, null, null, 0, 0, CertificateKind.SelfSigned, "review-fingerprint")
        show(certificate = certificate)
        rule.onAllNodesWithText(text(R.string.workspace_certificates)).filter(hasClickAction()).onFirst().performClick()
        rule.runOnIdle { model.select(certificate.id) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.detail is ApiResult.Success }
        rule.onNodeWithText(text(R.string.common_delete)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { failList = true }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.problemCode == "certificate.read_failed" }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.common_delete)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.certificates_detail_unavailable)).performScrollTo().assertIsDisplayed()
        rule.runOnIdle { model.action(owner, certificate, CertificateAction.Delete); assertEquals(0, writes); failList = false }
        rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.detail is ApiResult.Success }
        rule.onNodeWithText(text(R.string.common_delete)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun newLoginDismissesOldConfirmationAndOnlyNewConfirmationCanSubmit() {
        val certificate = ManagedCertificate(requireNotNull(receipt.certificateId), "host.example.test", listOf("host.example.test"),
            CertificateStatus.Issued, CertificateChallenge.DirectHttp01, 4102444800000L, null, null, null, 0,
            CertificateKey.EcdsaP256, null, null, null, null, 0, 0, CertificateKind.SelfSigned, "review-fingerprint")
        show(certificate = certificate)
        val originalOwner = owner
        rule.onAllNodesWithText(text(R.string.workspace_certificates)).filter(hasClickAction()).onFirst().performClick()
        rule.runOnIdle { model.select(certificate.id) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.detail is ApiResult.Success }
        rule.onNodeWithText(text(R.string.common_delete)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_delete_confirm)).assertIsDisplayed()
        rule.runOnUiThread { runBlocking {
            session.login(ServerConnectionIdentityRules.direct("https://certificate-review.invalid"), "review", "test".toCharArray()) {}
        } }
        rule.waitUntil(5_000) { !model.state.busy && model.state.list is ApiResult.Success && model.state.selectedId == null }
        rule.onNodeWithText(text(R.string.certificates_delete_confirm)).assertDoesNotExist()
        rule.onAllNodesWithText(text(R.string.workspace_certificates)).filter(hasClickAction()).onFirst().performClick()
        rule.runOnIdle { model.select(certificate.id) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.detail is ApiResult.Success }
        rule.runOnIdle { model.action(originalOwner, certificate, CertificateAction.Delete); assertEquals(0, writes) }
        rule.onNodeWithText(text(R.string.common_delete)).performScrollTo().performClick()
        rule.onAllNodesWithText(text(R.string.certificates_confirm)).filter(hasClickAction()).onFirst().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.uncertain }
        rule.runOnIdle { assertEquals(1, writes) }
    }
    @Test fun cancelConfirmationKeepsOriginalTargetWhenCurrentTaskChanges() {
        show()
        val original = receipt.copy(state = CertificateOperationState.Running, stage = "running")
        val next = original.copy(operationId = "33333333-3333-3333-3333-333333333333")
        rule.runOnIdle { tasks[original.operationId] = original; tasks[next.operationId] = next; model.recover(original.operationId) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.operationVerified }
        rule.onNodeWithText(text(R.string.workspace_operations)).performClick()
        rule.onNodeWithText(text(R.string.common_cancel)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_cancel_confirm)).assertIsDisplayed()
        rule.runOnIdle { model.recover(next.operationId) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.operation?.operationId == next.operationId }
        rule.onAllNodesWithText(text(R.string.certificates_confirm)).filter(hasClickAction()).onFirst().performClick()
        rule.runOnIdle { assertTrue(cancellations.isEmpty()); assertEquals(next, model.state.operation) }
        rule.onNodeWithText(text(R.string.common_cancel)).performScrollTo().performClick()
        rule.onAllNodesWithText(text(R.string.certificates_confirm)).filter(hasClickAction()).onFirst().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.operation?.state == CertificateOperationState.Cancelled }
        rule.runOnIdle { assertEquals(listOf(next.operationId), cancellations) }
    }
}
