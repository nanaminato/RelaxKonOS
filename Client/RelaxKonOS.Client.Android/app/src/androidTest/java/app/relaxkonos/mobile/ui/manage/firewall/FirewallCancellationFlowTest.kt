package app.relaxkonos.mobile.ui.manage.firewall

import android.graphics.Bitmap
import java.io.File
import androidx.activity.ComponentActivity
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.Lifecycle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.semantics.SemanticsActions
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

class FirewallCancellationFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: FirewallViewModel
    private var cancelRead = false
    private var writes = 0
    private var successfulWrite = false
    private var failVerification = false
    private var readFailure: Exception? = null
    private var statusReads = 0
    private val visible = mutableStateOf(true)
    private var delayRules = false
    private var pendingRules: kotlin.coroutines.Continuation<ApiResult<List<FirewallRule>>>? = null
    private var currentRules = emptyList<FirewallRule>()
    private var currentStatus = FirewallStatus(true, false, "ufw", null, "deny", "allow", "")
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val facts = FirewallFacts(FirewallStatus(true, false, "ufw", null, "deny", "allow", ""), emptyList())
    private fun show() {
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.FIREWALL), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun firewallStatus(serverUrl: String, accessToken: String): ApiResult<FirewallStatus> {
                statusReads++
                readFailure?.let { throw it }
                if (cancelRead) throw CancellationException("private cancelled read")
                return ApiResult.Success(currentStatus)
            }
            override suspend fun firewallRules(serverUrl: String, accessToken: String): ApiResult<List<FirewallRule>> =
                if (delayRules) kotlin.coroutines.suspendCoroutine { pendingRules = it }
                else ApiResult.Success(currentRules)
            override suspend fun changeFirewall(serverUrl: String, accessToken: String, change: FirewallChange): ApiResult<FirewallResult> {
                writes++
                if (!successfulWrite) throw CancellationException("private cancelled write")
                currentStatus = currentStatus.copy(defaultIncomingPolicy = change.incoming ?: currentStatus.defaultIncomingPolicy,
                    defaultOutgoingPolicy = change.outgoing ?: currentStatus.defaultOutgoingPolicy)
                if (failVerification) readFailure = IllegalStateException("private verification failure")
                return ApiResult.Success(FirewallResult(true, ""))
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://firewall-review.invalid"), "review", "test".toCharArray()) {} }
        val owner = session.state.value as SessionState.Active
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        rule.runOnUiThread {
            model = FirewallViewModel(session, FirewallRepository(gateway, session, elevation, FirewallMutationJournal(Storage())), ElevationAnswerProvider.Declines)
            store.put("firewall", model)
        }
        rule.setContent { MaterialTheme {
            if (visible.value) FirewallContent(model, owner, {}) else Text("Other page")
        } }
        rule.waitUntil(5_000) { model.state.facts != null && !model.state.busy }
    }
    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun refresh() = rule.onNodeWithText(text(R.string.common_refresh)).performScrollTo().performClick()
    private fun dismissFailure() {
        rule.onNodeWithText(text(R.string.common_dismiss)).assertIsDisplayed().performClick()
        rule.onNodeWithText(text(R.string.firewall_unverified)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.firewall_enable)).assertDoesNotExist()
    }
    @Test fun pausedConfirmationCannotWriteAndFreshConfirmationSubmitsOnce() {
        show()
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        val old = requireNotNull(model.confirmation)
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { assertNull(model.confirmation); model.change(old) }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertEquals(0, writes) }
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.firewall_submit)).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.size == 1 }
        rule.runOnIdle { model.change(old); assertEquals(1, writes) }
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
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun leavingAndReopeningRejectsNonCooperativeOldRead() {
        show()
        rule.runOnIdle { delayRules = true }
        refresh()
        rule.waitUntil(5_000) { pendingRules != null }
        rule.runOnIdle { visible.value = false }
        rule.onNodeWithText("Other page").assertIsDisplayed()
        rule.runOnIdle {
            assertNull(model.state.facts)
            delayRules = false
            currentStatus = currentStatus.copy(defaultIncomingPolicy = "allow")
            visible.value = true
        }
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts?.status == currentStatus }
        rule.runOnUiThread { pendingRules!!.resumeWith(Result.success(ApiResult.Success(emptyList()))) }
        rule.waitForIdle()
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().assertIsEnabled()
        rule.runOnIdle {
            assertEquals(currentStatus, model.state.facts?.status)
            assertNull(model.state.problem)
            assertEquals(0, writes)
        }
    }
    @Test fun cancelledChangeRetainsMarkerAndRefreshDoesNotReplayWrite() {
        show()
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().performClick()
        rule.onNode(hasClickAction() and hasText(text(R.string.firewall_submit))).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isNotEmpty() }
        dismissFailure()
        refresh()
        rule.waitUntil(5_000) { !model.state.busy && model.state.facts != null }
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, writes); assertEquals(1, model.state.pending.size) }
    }
    private fun submitChangedDefaults() {
        rule.onNodeWithText(text(R.string.firewall_defaults)).performScrollTo().performClick()
        rule.onAllNodesWithText(text(R.string.firewall_reject)).onFirst().performClick()
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        rule.onNodeWithText(text(R.string.firewall_submit)).performClick()
    }
    @Test fun successfulDefaultsCloseEditorUsingTheSingleVerification() {
        show()
        val initialReads = statusReads
        successfulWrite = true
        submitChangedDefaults()
        rule.waitUntil(5_000) { !model.state.busy && model.state.saved == 1 }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.firewall_defaults)).performScrollTo().assertIsEnabled()
        rule.runOnIdle {
            assertEquals(3, statusReads - initialReads)
            assertEquals("reject", model.state.facts?.status?.defaultIncomingPolicy)
            assertNull(model.state.problem)
            assertTrue(model.state.pending.isEmpty()); assertEquals(1, writes)
        }
    }
    @Test fun knownSuccessWithFailedVerificationClosesDraftAndRequiresExplicitAdoption() {
        show()
        successfulWrite = true; failVerification = true
        submitChangedDefaults()
        rule.waitUntil(5_000) { !model.state.busy && model.state.saved == 1 }
        rule.onNodeWithText(text(R.string.firewall_refresh_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_save)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.workspace_records_attention)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.firewall_accept_facts)).performScrollTo().assertIsEnabled()
        rule.runOnIdle { readFailure = null }
        rule.onNodeWithText(text(R.string.firewall_accept_facts)).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isEmpty() }
        rule.onNodeWithText(text(R.string.workspace_records_empty)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, writes); assertEquals("reject", model.state.facts?.status?.defaultIncomingPolicy) }
    }
    @Test fun repeatedImmediateReadFailureReopensDismissedFeedback() {
        show()
        rule.runOnIdle { cancelRead = true; model.refresh() }
        rule.onNode(isDialog()).assertExists()
        val dismiss = rule.onNodeWithText(text(R.string.common_dismiss)).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.runOnIdle { dismiss(); model.refresh() }
        rule.onNode(isDialog()).assertExists()
        rule.onNodeWithText(text(R.string.common_dismiss)).assertIsDisplayed()
        rule.runOnIdle { assertNull(model.state.facts); assertFalse(model.state.busy); assertEquals(0, writes) }
    }

    @Test fun oldCancelCannotDismissReplacementOrReopenedConfirmation() {
        show()
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().performClick()
        val oldCancel = requireNotNull(rule.onNodeWithText(text(R.string.common_cancel))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action)
        val original = requireNotNull(model.confirmation)
        rule.runOnIdle {
            model.requestChange(original.owner, original.expected,
                FirewallChange(FirewallChangeKind.Defaults, incoming = "reject", outgoing = "allow"))
        }
        val replacement = requireNotNull(model.confirmation)
        rule.runOnIdle { oldCancel(); assertSame(replacement, model.confirmation); assertEquals(0, writes) }
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertNull(model.confirmation) }
        rule.onNodeWithText(text(R.string.firewall_enable)).performScrollTo().performClick()
        val reopened = requireNotNull(model.confirmation)
        rule.runOnIdle { oldCancel(); assertSame(reopened, model.confirmation); assertEquals(0, writes) }
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertNull(model.confirmation) }
    }

    @Test fun deleteReviewShowsRuleEndpointsProtocolAndAddressFamily() {
        val target = FirewallRule(7, "allow", "in", "tcp", "2001:db8:1234:5678::1/128", "2001:db8:abcd:ffff::2/128", "5000", "IPv6")
        currentRules = listOf(target)
        show()
        rule.onNode(hasText("7 ·", substring = true)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.common_delete)).performScrollTo().performClick()
        listOf(target.source, target.destination, target.port).forEach { value ->
            rule.onNode(hasText(value) and hasAnyAncestor(isDialog())).performScrollTo().assertIsDisplayed()
            if (value == target.source) captureReview("firewall-delete-source.png")
        }
        rule.onNode(hasText("TCP", substring = true) and hasAnyAncestor(isDialog())).performScrollTo().assertIsDisplayed()
        rule.onNode(hasText(target.addressFamily, substring = true) and hasAnyAncestor(isDialog())).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.firewall_number, target.number)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.firewall_submit)).assertIsDisplayed()
        captureReview("firewall-delete-target.png")
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertEquals(0, writes); assertNull(model.confirmation) }
    }

    private fun captureReview(name: String) {
        rule.onNodeWithText(text(R.string.firewall_confirm)).assertIsDisplayed()
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        automation.waitForIdle(500, 5_000)
        val root = requireNotNull(automation.rootInActiveWindow)
        assertEquals(context.packageName, root.packageName.toString())
        val bitmap = requireNotNull(automation.takeScreenshot())
        try {
            File(context.getExternalFilesDir(null), name).outputStream().use {
                assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG, 100, it))
            }
        } finally { bitmap.recycle() }
    }

}

