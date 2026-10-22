package app.relaxkonos.mobile.ui.manage.proxy

import android.graphics.Bitmap
import java.io.File
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.compose.ui.test.*
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelStore
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.After
import org.junit.Rule
import org.junit.Test

class ProxyTaskFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val pollClockTimes = java.util.concurrent.CopyOnWriteArrayList<Long>()
    private val store = ViewModelStore()
    private lateinit var model: ProxyViewModel
    private lateinit var owner: SessionState.Active
    private lateinit var journal: ProxyRequestJournal
    private lateinit var installationJournal: InstallationRequestJournal
    private lateinit var index: OperationIndex
    private var installationService = InstallationService.Mihomo
    private var overviewReads = 0
    private var failOverview = false
    private val install = InstallationOperation("44444444-4444-4444-4444-444444444444", InstallationService.Mihomo,
        InstallationKind.Install, InstallationState.Running, InstallationStage.Downloading, 20, null, 1, 1, null, true)
    private var current = install
    private val operation = ProxyOperation("55555555-5555-5555-5555-555555555555", "start", ProxyOperationState.Running, "starting", "")
    private val overview = ProxyOverview(ProxyRuntime("managed", ProxyRuntimeState.Running, "1", null, true, ""),
        null, true, true, "healthy", "", false, true, false, "linux", false, ProxySystemProxyCapabilities(true, false, true, false), supportsLogs = true)
    private val sent = mutableListOf<String>()
    private val queued = mutableListOf<ProxyAction>()
    private val profileSaves = mutableListOf<String>()
    private val imports = mutableListOf<ProxyImportRequest>()
    private val yamlSaves = mutableListOf<String>()
    private var terminalRecovery = false
    private val operationTargets = mutableListOf<String>()
    private var showProfile = false
    private var downloadOptions = false
    private val profile = ProxyProfile("77777777-7777-7777-7777-777777777777", "review-profile", "mihomo", false, 1)
    @Volatile private var operationReads = 0
    @Volatile private var installationReads = 0
    @Volatile private var logReads = 0
    private var logFailure = false
    private var tasks = true
    private fun storage() = object : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    @After fun cleanup() { rule.mainClock.autoAdvance = true; rule.runOnUiThread { store.clear() } }
    private fun start(withTasks: Boolean = true) {
        tasks = withTasks
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.PROXY), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun proxyOverview(serverUrl: String, accessToken: String): ApiResult<ProxyOverview> {
                overviewReads++
                return if (failOverview) ApiResult.Transport(null) else ApiResult.Success(overview)
            }
            override suspend fun proxyProfiles(serverUrl: String, accessToken: String): ApiResult<List<ProxyProfile>> = ApiResult.Success(if (showProfile) listOf(profile) else emptyList())
            override suspend fun saveProxyProfile(serverUrl: String, accessToken: String, id: String?, request: ProxyProfileRequest): ApiResult<ProxyProfile> {
                profileSaves += request.name
                return ApiResult.Success(ProxyProfile("77777777-7777-7777-7777-777777777777", request.name, "mihomo", false, 1))
            }
            override suspend fun importProxySubscription(serverUrl: String, accessToken: String, request: ProxyImportRequest): ApiResult<ProxySubscription> {
                imports += request
                return ApiResult.Success(ProxySubscription("88888888-8888-8888-8888-888888888888", request.name.orEmpty(), profile.id, false, null))
            }
            override suspend fun applyProxyConfiguration(serverUrl: String, accessToken: String, id: String, yaml: String): ApiResult<Unit> {
                assertEquals(profile.id, id); yamlSaves += yaml; return ApiResult.Success(Unit)
            }
            override suspend fun proxySubscriptions(serverUrl: String, accessToken: String): ApiResult<List<ProxySubscription>> = ApiResult.Success(emptyList())
            override suspend fun proxyRouting(serverUrl: String, accessToken: String) = ApiResult.Success(ProxyRoutingMode.Rule)
            override suspend fun proxyDownloadOptions(serverUrl: String, accessToken: String) = ApiResult.Success(downloadOptions)
            override suspend fun proxySettings(serverUrl: String, accessToken: String) = ApiResult.Success(ProxySettings(false, false, true, false, false, "info", 7890, false, "127.0.0.1", null, null))
            override suspend fun proxyRecovery(serverUrl: String, accessToken: String) = ApiResult.Success(ProxyRecovery(false, false, null, ""))
            override suspend fun runtimeResources(serverUrl: String, accessToken: String) = ApiResult.Success(ManagedRuntimeResources(null, emptyList(), "runtime.installation_absent"))
            override suspend fun proxyOperation(serverUrl: String, accessToken: String, id: String): ApiResult<ProxyOperation> {
                pollClockTimes += rule.mainClock.currentTime; operationReads++; operationTargets += id; return ApiResult.Success(if (terminalRecovery) operation.copy(operationId = id, state = ProxyOperationState.Succeeded) else operation)
            }
            override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService): ApiResult<InstallationOperation?> = ApiResult.Success(if (tasks) current else null)
            override suspend fun installation(serverUrl: String, accessToken: String, operationId: String): ApiResult<InstallationOperation> {
                pollClockTimes += rule.mainClock.currentTime; installationReads++; return ApiResult.Success(current.copy(service = installationService))
            }
            override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<InstallationOperation> {
                sent += operationId; return ApiResult.Transport(null)
            }
            override suspend fun proxyQueue(serverUrl: String, accessToken: String, action: ProxyAction, target: String?, key: String): ApiResult<String> {
                queued += action; return ApiResult.Transport(null)
            }
            override suspend fun proxyLogs(serverUrl: String, accessToken: String): ApiResult<List<ProxyLog>> {
                logReads++; return if (logFailure) ApiResult.Transport(null) else ApiResult.Success(listOf(ProxyLog(1, "info", "review-log")))
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://proxy-review.invalid"), "review", "test".toCharArray()) {} }
        owner = session.state.value as SessionState.Active
        index = OperationIndex(object : OperationIndexStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        val elevation = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        journal = ProxyRequestJournal(storage())
        installationJournal = InstallationRequestJournal(storage())
        rule.runOnUiThread {
            model = ProxyViewModel(session, ProxyRepository(gateway, session, index, journal),
                InstallationRepository(gateway, session, elevation, index, installationJournal),
                index, ElevationAnswerProvider.Declines)
            store.put("proxy", model)
        }
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                ProxyContent(model, owner, {}, modifier = Modifier.widthIn(max = 360.dp).fillMaxHeight().statusBarsPadding())
            }
        } }
        rule.waitUntil(5_000) { !model.state.busy && model.state.overview is ApiResult.Success }
    }
    private fun tab(id: Int) = rule.onAllNodesWithText(text(id)).filter(hasClickAction()).onFirst()
        .performScrollTo().performClick().assertIsSelected()
    private fun prepareAdoptionRecord() {
        start(withTasks = false)
        rule.runOnIdle { journal.begin(owner, null, null, ProxyWrite.Profile); model.refresh() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.size == 1 }
        tab(R.string.workspace_records)
    }
    private fun openAdoptionReview() {
        prepareAdoptionRecord()
        rule.onNodeWithText(text(R.string.mihomo_accept_facts)).performScrollTo().performClick()
    }
    @Test fun pausedAdoptionRejectsOldOpenerAndConfirmAfterResume() {
        prepareAdoptionRecord()
        val oldOpen = rule.onNodeWithText(text(R.string.mihomo_accept_facts)).performScrollTo()
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.onNodeWithText(text(R.string.mihomo_accept_facts)).performClick()
        val oldConfirm = rule.onNode(hasClickAction() and hasText(text(R.string.tunnels_confirm)))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        val reads = overviewReads
        val armed = java.util.concurrent.atomic.AtomicBoolean(false)
        val rejectedAtResume = java.util.concurrent.atomic.AtomicBoolean(false)
        val lifecycle = rule.activity.lifecycle
        val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME && armed.get()) {
                oldOpen(); oldConfirm()
                rejectedAtResume.set(model.adoptionReview == null && overviewReads == reads)
            }
        }
        rule.runOnUiThread { lifecycle.addObserver(observer) }
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { oldOpen(); oldConfirm(); assertNull(model.adoptionReview); assertEquals(reads, overviewReads) }
            armed.set(true)
            rule.mainClock.autoAdvance = true
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
            assertTrue(rejectedAtResume.get())
        } finally {
            rule.mainClock.autoAdvance = true
            rule.runOnUiThread { lifecycle.removeObserver(observer) }
        }
        rule.runOnIdle { oldOpen(); oldConfirm(); assertNull(model.adoptionReview); assertEquals(reads, overviewReads) }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.mihomo_accept_facts)).performScrollTo().performClick()
        rule.onNode(hasClickAction() and hasText(text(R.string.tunnels_confirm))).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isEmpty() }
        rule.runOnIdle { assertTrue(model.state.overview is ApiResult.Success); assertTrue(sent.isEmpty()) }
    }
    @Test fun oldAdoptionCancelCannotCloseAReopenedReview() {
        openAdoptionReview()
        val oldCancel = rule.onNodeWithText(text(R.string.common_cancel))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText(text(R.string.mihomo_accept_facts)).performScrollTo().performClick()
        rule.runOnIdle { oldCancel() }
        rule.onNode(isDialog()).assertExists()
        rule.runOnIdle { assertEquals(1, model.state.pending.size); assertTrue(sent.isEmpty()) }
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertEquals(1, model.state.pending.size) }
    }
    @Test fun oldAdoptionConfirmCannotConsumeAReopenedReview() {
        openAdoptionReview()
        val oldConfirm = rule.onNode(hasClickAction() and hasText(text(R.string.tunnels_confirm)))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText(text(R.string.mihomo_accept_facts)).performScrollTo().performClick()
        val reads = overviewReads
        rule.runOnIdle { oldConfirm(); assertEquals(reads, overviewReads); assertEquals(1, model.state.pending.size) }
        rule.onNode(isDialog()).assertExists()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertEquals(reads, overviewReads); assertEquals(1, model.state.pending.size) }
    }
    @Test fun failedAdoptionCanBeRetriedWithoutPublishingReplacementFacts() {
        start(withTasks = false)
        rule.runOnIdle { journal.begin(owner, null, null, ProxyWrite.Profile); model.refresh() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.size == 1 }
        tab(R.string.workspace_records)
        fun adopt() {
            rule.onNodeWithText(text(R.string.mihomo_accept_facts)).performScrollTo().assertIsEnabled().performClick()
            rule.onNode(hasClickAction() and hasText(text(R.string.tunnels_confirm))).performClick()
        }
        val before = overviewReads
        rule.runOnIdle { failOverview = true }
        adopt()
        rule.waitUntil(5_000) { !model.state.busy && model.state.uncertain }
        rule.onNodeWithText(text(R.string.mihomo_uncertain)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(before + 1, overviewReads); assertNull(model.state.overview); assertEquals(1, model.state.pending.size) }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        adopt()
        rule.waitUntil(5_000) { !model.state.busy && model.state.uncertain }
        rule.onNodeWithText(text(R.string.mihomo_uncertain)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(before + 2, overviewReads); assertNull(model.state.overview); assertEquals(1, model.state.pending.size) }
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.runOnIdle { failOverview = false }
        adopt()
        rule.waitUntil(5_000) { !model.state.busy && model.state.pending.isEmpty() }
        rule.runOnIdle { assertTrue(model.state.overview is ApiResult.Success); assertFalse(model.state.uncertain); assertNull(model.state.problemCode); assertTrue(sent.isEmpty()) }
    }
    @Test fun cancellationIsWithdrawnOnPauseAndReplacementRequiresNewConfirmation() {
        start(); tab(R.string.workspace_records)
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction()).performScrollTo().performClick()
        val old = requireNotNull(model.installationCancellation)
        val oldDismiss = rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction() and hasAnyAncestor(isDialog()))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        val oldSubmit = rule.onNode(hasText(text(R.string.tunnels_confirm)) and hasClickAction())
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        val before = installationReads
        Thread.sleep(2_000)
        rule.runOnIdle { assertEquals(before, installationReads); assertSame(old, model.installationCancellation) }
        rule.mainClock.autoAdvance = false
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.runOnUiThread { assertNull(model.installationCancellation); model.cancelInstall(old); assertTrue(sent.isEmpty()) }
        rule.mainClock.autoAdvance = true
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction()).performScrollTo().performClick()
        val replaced = requireNotNull(model.installationCancellation)
        rule.runOnIdle {
            oldDismiss(); oldSubmit()
            assertSame(replaced, model.installationCancellation); assertTrue(sent.isEmpty())
        }
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertIsDisplayed()
        rule.runOnUiThread { current = install.copy(operationId = "66666666-6666-6666-6666-666666666666"); model.refresh() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installationVerified }
        rule.onNodeWithText(text(R.string.operations_cancel_explanation)).assertDoesNotExist()
        rule.runOnIdle { model.cancelInstall(replaced); assertTrue(sent.isEmpty()) }
        rule.onNode(hasText(text(R.string.common_cancel)) and hasClickAction()).performScrollTo().performClick()
        rule.onAllNodesWithText(current.operationId).onLast().assertIsDisplayed()
        rule.waitForIdle()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        instrumentation.uiAutomation.waitForIdle(500, 5_000)
        val screenshot = instrumentation.uiAutomation.takeScreenshot()
        try {
            File(instrumentation.targetContext.getExternalFilesDir(null), "proxy-cancellation-review.png").outputStream().use {
                screenshot.compress(Bitmap.CompressFormat.PNG, 100, it)
            }
        } finally { screenshot.recycle() }
        rule.onNode(hasText(text(R.string.tunnels_confirm)) and hasClickAction()).performClick()
        rule.waitUntil(5_000) { !model.state.busy && sent.size == 1 }
        rule.runOnIdle { assertEquals(listOf(current.operationId), sent); assertFalse(model.state.installationVerified) }
    }
    private fun openStartConfirmation() {
        rule.onNode(hasText(text(R.string.mihomo_state_start)) and hasClickAction()).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
    }
    private fun dialogButton(id: Int) = rule.onNode(hasText(text(id)) and hasClickAction() and hasAnyAncestor(isDialog()))
    @Test fun oldRuntimeDismissCannotCloseReopenedConfirmation() {
        start(withTasks = false); openStartConfirmation()
        val oldDismiss = dialogButton(R.string.common_cancel).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        dialogButton(R.string.common_cancel).performClick(); openStartConfirmation()
        rule.runOnIdle { oldDismiss(); assertTrue(queued.isEmpty()) }
        rule.onNode(isDialog()).assertExists()
        dialogButton(R.string.common_cancel).performClick()
        rule.onNode(isDialog()).assertDoesNotExist()
    }
    @Test fun oldRuntimeSubmitCannotSendAfterReopenOrPause() {
        start(withTasks = false)
        val oldOpen = rule.onNode(hasText(text(R.string.mihomo_state_start)) and hasClickAction())
            .performScrollTo().fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        openStartConfirmation()
        val oldSubmit = dialogButton(R.string.tunnels_confirm).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        dialogButton(R.string.common_cancel).performClick(); openStartConfirmation()
        rule.runOnIdle { oldSubmit(); assertTrue(queued.isEmpty()) }
        rule.onNode(isDialog()).assertExists()
        val pausedSubmit = dialogButton(R.string.tunnels_confirm).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.mainClock.autoAdvance = false
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.runOnUiThread { pausedSubmit(); assertTrue(queued.isEmpty()) }
        rule.mainClock.autoAdvance = true
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.runOnIdle { oldOpen(); pausedSubmit(); assertNull(model.changeReview); assertTrue(queued.isEmpty()) }
        openStartConfirmation(); dialogButton(R.string.tunnels_confirm).performClick()
        rule.waitUntil(5_000) { !model.state.busy && queued.size == 1 }
        rule.runOnIdle { oldSubmit(); pausedSubmit(); assertEquals(listOf(ProxyAction.Start), queued) }
    }
    private fun openProfileEditor(name: String) {
        rule.onNodeWithText(text(R.string.mihomo_create_profile)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.mihomo_name)).performTextReplacement(name)
        androidx.test.espresso.Espresso.closeSoftKeyboard()
    }
    private fun discardButton(id: Int) = rule.onNode(hasText(text(id)) and hasClickAction() and
        hasAnyAncestor(isDialog() and hasAnyDescendant(hasText(text(R.string.mihomo_discard)))))
    @Test fun oldEditorDiscardCancelCannotCloseReopenedDecision() {
        start(withTasks = false); tab(R.string.workspace_profiles); openProfileEditor("draft-one")
        dialogButton(R.string.common_cancel).performClick()
        val oldCancel = discardButton(R.string.common_cancel).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        val oldDiscard = discardButton(R.string.common_close).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        discardButton(R.string.common_cancel).performClick()
        dialogButton(R.string.common_cancel).performClick()
        rule.runOnIdle { oldCancel(); oldDiscard(); assertTrue(profileSaves.isEmpty()) }
        rule.onNodeWithText(text(R.string.mihomo_discard)).assertIsDisplayed()
        discardButton(R.string.common_cancel).performClick()
        rule.onNodeWithText("draft-one").assertExists()
    }
    @Test fun closedEditorSaveCannotSubmitOrReplaceNewDraft() {
        start(withTasks = false); tab(R.string.workspace_profiles); openProfileEditor("discarded-draft")
        val oldSave = dialogButton(R.string.common_save).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        dialogButton(R.string.common_cancel).performClick(); discardButton(R.string.common_close).performClick()
        rule.runOnIdle { oldSave(); assertTrue(profileSaves.isEmpty()) }
        openProfileEditor("current-draft")
        rule.runOnIdle { oldSave(); assertTrue(profileSaves.isEmpty()) }
        rule.onNodeWithText("current-draft").assertExists()
        dialogButton(R.string.common_save).performClick()
        rule.waitUntil(5_000) { !model.state.busy && profileSaves.size == 1 }
        rule.runOnIdle { oldSave(); assertEquals(listOf("current-draft"), profileSaves) }
    }
    @Test fun editorPauseKeepsDraftAndRejectsOldSaveCloseAndOpener() {
        start(withTasks = false); tab(R.string.workspace_profiles)
        val oldOpen = rule.onNodeWithText(text(R.string.mihomo_create_profile)).performScrollTo()
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        openProfileEditor("first-name")
        val staleSnapshot = dialogButton(R.string.common_save).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.onNodeWithText(text(R.string.mihomo_name)).performTextReplacement("retained-name")
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        rule.runOnIdle { staleSnapshot(); assertTrue(profileSaves.isEmpty()) }
        val oldSave = dialogButton(R.string.common_save).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        val oldClose = dialogButton(R.string.common_cancel).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        val oldField = rule.onNodeWithText(text(R.string.mihomo_name)).fetchSemanticsNode().config[SemanticsActions.SetText].action!!
        rule.mainClock.autoAdvance = false
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.runOnUiThread { oldSave(); oldClose(); oldField(androidx.compose.ui.text.AnnotatedString("stale-name")); assertTrue(profileSaves.isEmpty()) }
        rule.mainClock.autoAdvance = true
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.runOnIdle { oldOpen(); oldSave(); oldClose(); oldField(androidx.compose.ui.text.AnnotatedString("late-name")); assertTrue(profileSaves.isEmpty()) }
        rule.onNodeWithText(text(R.string.mihomo_discard)).assertDoesNotExist()
        rule.onNodeWithText("retained-name").assertExists()
        val currentSave = dialogButton(R.string.common_save).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.runOnIdle { currentSave(); currentSave() }
        rule.waitUntil(5_000) { !model.state.busy && profileSaves.size == 1 }
        rule.onNodeWithText(text(R.string.mihomo_name)).assertDoesNotExist()
        rule.runOnIdle { currentSave(); assertEquals(listOf("retained-name"), profileSaves) }
    }
    @Test fun subscriptionPausePreservesSourceRouteAndConsentWithoutOldSubmit() {
        downloadOptions = true
        start(withTasks = false); tab(R.string.workspace_profiles)
        rule.onNode(hasText(text(R.string.mihomo_import)) and hasClickAction()).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.mihomo_name)).performTextReplacement("review-subscription")
        val source = "https://subscription.invalid/feed?token=synthetic-only"
        rule.onNodeWithText(text(R.string.mihomo_source_url)).performTextReplacement(source)
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.mihomo_download_system)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.mihomo_apply_confirm)).performScrollTo().performClick()
        val oldSave = dialogButton(R.string.common_save).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.mainClock.autoAdvance = false
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.runOnUiThread { oldSave(); assertTrue(imports.isEmpty()) }
        rule.mainClock.autoAdvance = true
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.runOnIdle { oldSave(); assertTrue(imports.isEmpty()) }
        rule.onNodeWithText(text(R.string.mihomo_download_system)).performScrollTo().assertIsSelected()
        rule.onNodeWithText(text(R.string.mihomo_apply_confirm)).performScrollTo().assertIsOn()
        dialogButton(R.string.common_save).assertIsEnabled().performClick()
        rule.waitUntil(5_000) { !model.state.busy && imports.size == 1 }
        rule.runOnIdle {
            oldSave(); assertEquals(1, imports.size)
            assertEquals(source, imports.single().url); assertEquals("review-subscription", imports.single().name)
            assertEquals(ProxyDownloadRoute.SystemProxy, imports.single().route)
        }
    }
    @Test fun yamlOldSnapshotDoesNotSubmitEditedContent() {
        showProfile = true
        start(withTasks = false); tab(R.string.workspace_profiles)
        rule.onNodeWithText(profile.name).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.mihomo_apply_yaml)) and hasClickAction()).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.mihomo_yaml)).performTextReplacement("mode: rule")
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.mihomo_apply_confirm)).performScrollTo().performClick()
        val oldSave = dialogButton(R.string.common_save).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.onNodeWithText(text(R.string.mihomo_yaml)).performTextReplacement("mode: direct")
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        rule.runOnIdle { oldSave(); assertTrue(yamlSaves.isEmpty()) }
        dialogButton(R.string.common_save).performClick()
        rule.waitUntil(5_000) { !model.state.busy && yamlSaves.size == 1 }
        rule.runOnIdle { oldSave(); assertEquals(listOf("mode: direct"), yamlSaves) }
    }
    private fun openOperationRecovery(id: String = operation.operationId) {
        rule.onNodeWithText(text(R.string.mihomo_recover_operation)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_operation_id)).performTextReplacement(id)
        androidx.test.espresso.Espresso.closeSoftKeyboard()
    }
    @Test fun oldRecoveryCloseCannotCloseReopenedForm() {
        terminalRecovery = true
        start(withTasks = false); tab(R.string.workspace_records); openOperationRecovery()
        val oldClose = dialogButton(R.string.common_cancel).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        dialogButton(R.string.common_cancel).performClick(); openOperationRecovery()
        rule.runOnIdle { oldClose(); assertTrue(operationTargets.isEmpty()) }
        rule.onNodeWithText(text(R.string.certificates_operation_id)).assertExists()
        dialogButton(R.string.common_cancel).performClick()
    }
    @Test fun oldRecoverySubmitCannotQueryReplacementDraftOrAfterPause() {
        terminalRecovery = true
        start(withTasks = false); tab(R.string.workspace_records); openOperationRecovery()
        val oldSubmit = dialogButton(R.string.common_refresh).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        dialogButton(R.string.common_cancel).performClick(); openOperationRecovery(install.operationId)
        rule.runOnIdle { oldSubmit(); assertTrue(operationTargets.isEmpty()) }
        val oldField = rule.onNodeWithText(text(R.string.certificates_operation_id)).fetchSemanticsNode().config[SemanticsActions.SetText].action!!
        val pausedSubmit = dialogButton(R.string.common_refresh).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.mainClock.autoAdvance = false
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.runOnUiThread { pausedSubmit(); assertTrue(operationTargets.isEmpty()) }
        rule.mainClock.autoAdvance = true
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.runOnIdle { pausedSubmit(); oldField(androidx.compose.ui.text.AnnotatedString(operation.operationId)); assertTrue(operationTargets.isEmpty()) }
        rule.onNodeWithText(install.operationId).assertExists()
        captureRecoveryForm("proxy-operation-recovery-form.png")
        dialogButton(R.string.common_refresh).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.operationVerified }
        rule.runOnIdle { oldSubmit(); pausedSubmit(); assertEquals(listOf(install.operationId), operationTargets) }
    }
    private fun captureRecoveryForm(name: String) {
        rule.waitForIdle()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        instrumentation.uiAutomation.waitForIdle(500, 5_000)
        val screenshot = instrumentation.uiAutomation.takeScreenshot()
        try { File(instrumentation.targetContext.getExternalFilesDir(null), name).outputStream().use {
            screenshot.compress(Bitmap.CompressFormat.PNG, 100, it)
        } } finally { screenshot.recycle() }
    }
    @Test fun installationRecoveryConsentBelongsToCurrentIdAndOriginalRequest() {
        start(withTasks = false)
        rule.runOnIdle { installationJournal.begin(owner, InstallationService.Mihomo, InstallationKind.Install, "synthetic-original"); model.refresh() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.pendingInstallation }
        tab(R.string.workspace_records)
        rule.onNodeWithText(text(R.string.mihomo_recover_installation)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_operation_id)).performTextReplacement(install.operationId)
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        dialogButton(R.string.common_refresh).assertIsNotEnabled()
        val oldConsent = rule.onNodeWithText(text(R.string.mihomo_identify)).performScrollTo()
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.onNodeWithText(text(R.string.certificates_operation_id)).performTextReplacement(operation.operationId)
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        rule.runOnIdle { oldConsent(); assertEquals(0, installationReads) }
        rule.onNodeWithText(text(R.string.mihomo_identify)).performScrollTo().assertIsOff()
        rule.onNodeWithText(text(R.string.certificates_operation_id)).performTextReplacement(install.operationId)
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.mihomo_identify)).performScrollTo().performClick()
        val oldSubmit = dialogButton(R.string.common_refresh).fetchSemanticsNode().config[SemanticsActions.OnClick].action!!
        rule.onNodeWithText(text(R.string.certificates_operation_id)).performTextReplacement(operation.operationId)
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        rule.runOnIdle { oldSubmit(); assertEquals(0, installationReads); assertTrue(model.state.pendingInstallation) }
        rule.onNodeWithText(text(R.string.mihomo_identify)).performScrollTo().assertIsOff()
        dialogButton(R.string.common_refresh).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.certificates_operation_id)).performTextReplacement(install.operationId)
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.mihomo_identify)).performScrollTo().performClick()
        captureRecoveryForm("proxy-installation-recovery-form.png")
        dialogButton(R.string.common_refresh).assertIsEnabled().performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.installationVerified && !model.state.pendingInstallation }
        rule.runOnIdle { oldSubmit(); assertEquals(1, installationReads); assertTrue(installationJournal.pending(owner).isEmpty()); assertTrue(sent.isEmpty()) }
    }
    @Test fun mihomoRecoveryCannotRevealOrResolveAnotherServicesReceipt() {
        installationService = InstallationService.Nginx
        start(withTasks = false)
        rule.runOnIdle {
            val pending = installationJournal.begin(owner, InstallationService.Nginx, InstallationKind.Install, "synthetic-nginx")
            installationJournal.update(pending.copy(operationId = install.operationId))
            index.record(owner, OperationDomain.Installation, InstallationService.Nginx.name, install.operationId)
            index.hide(owner, index.forOwner(owner).single())
        }
        tab(R.string.workspace_records)
        rule.onNodeWithText(text(R.string.mihomo_recover_installation)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.certificates_operation_id)).performTextReplacement(install.operationId)
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        dialogButton(R.string.common_refresh).performClick()
        rule.waitUntil(5_000) { !model.state.busy && model.state.uncertain }
        rule.runOnIdle {
            assertEquals(1, installationReads); assertNull(model.state.installation); assertFalse(model.state.installationVerified)
            assertEquals(InstallationService.Nginx, installationJournal.pending(owner).single().service)
            assertTrue(index.forOwner(owner).isEmpty()); assertTrue(sent.isEmpty())
        }
    }
    @Test fun recoveryFormPausesBothTaskObservationsUntilClosed() {
        start(); tab(R.string.workspace_records)
        rule.runOnUiThread { model.recoverOperation(operation.operationId) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.operationVerified }
        openOperationRecovery()
        val beforeOperations = operationReads; val beforeInstallations = installationReads
        Thread.sleep(1_800)
        rule.runOnIdle {
            assertNotNull(model.recoveryForm); assertFalse(model.state.busy)
            assertEquals(beforeOperations, operationReads); assertEquals(beforeInstallations, installationReads)
        }
        dialogButton(R.string.common_cancel).performClick()
        rule.waitUntil(8_000) { operationReads > beforeOperations && installationReads > beforeInstallations && !model.state.busy }
    }
    @Test fun backgroundStopsBothTaskQueriesAndForegroundResumesBoth() {
        start()
        rule.runOnUiThread { model.recoverOperation(operation.operationId) }
        rule.waitUntil(5_000) { !model.state.busy && model.state.operationVerified }
        val originalOperations = operationReads; val originalInstalls = installationReads
        rule.waitUntil(8_000) { operationReads > originalOperations && installationReads > originalInstalls && !model.state.busy }
        val lifecycle = rule.activity.lifecycle
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.runOnUiThread { assertEquals(Lifecycle.State.STARTED, lifecycle.currentState); assertFalse(model.state.busy) }
        val pausedOperations = operationReads; val pausedInstalls = installationReads
        Thread.sleep(2_000)
        assertEquals(pausedOperations, operationReads); assertEquals(pausedInstalls, installationReads)
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(8_000) { operationReads > pausedOperations && installationReads > pausedInstalls && !model.state.busy }
    }
    @Test fun logsStopInBackgroundAndFailureRecoversOnNextForegroundSample() {
        start(false); tab(R.string.workspace_logs)
        rule.waitUntil(5_000) { logReads > 0 && !model.state.diagnosticsBusy }
        rule.onNodeWithText("review-log", substring = true).assertExists()
        val lifecycle = rule.activity.lifecycle
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.runOnUiThread { assertEquals(Lifecycle.State.STARTED, lifecycle.currentState) }
        val paused = logReads
        Thread.sleep(3_300)
        assertEquals(paused, logReads)
        rule.runOnUiThread { logFailure = true }
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(5_000) { logReads > paused && model.state.diagnosticsFailed && !model.state.diagnosticsBusy }
        rule.runOnUiThread { logFailure = false }
        val failed = logReads
        rule.waitUntil(8_000) { logReads > failed && !model.state.diagnosticsFailed && !model.state.diagnosticsBusy }
        rule.onNodeWithText("review-log", substring = true).assertExists()
        tab(R.string.workspace_overview)
        val left = logReads
        Thread.sleep(3_300)
        assertEquals(left, logReads)
        rule.runOnIdle { assertTrue(sent.isEmpty()) }
    }
    @Test fun identicalSnapshotsContinuePollingWithoutBusyFrames() {
        start()
        rule.runOnUiThread { model.recoverOperation(operation.operationId) }; rule.waitUntil(5_000) { !model.state.busy && model.state.operationVerified }
        rule.waitForIdle()
        val initialOperations = operationReads
        val initialInstallations = installationReads
        rule.waitUntil(10_000) { operationReads >= initialOperations + 3 && installationReads >= initialInstallations + 3 }
        rule.runOnIdle { assertTrue(sent.isEmpty()); assertTrue(model.state.installationVerified) }
    }

    @Test fun pausedTaskObservationRestartsWithFullInterval() {
        start()
        rule.runOnUiThread { model.recoverOperation(operation.operationId) }; rule.waitUntil(5_000) { !model.state.busy && model.state.operationVerified }
        rule.waitForIdle()
        var paused = 0
        val resumedAt = java.util.concurrent.atomic.AtomicLong(-1L)
        val lifecycle = rule.activity.lifecycle
        val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) resumedAt.set(rule.mainClock.currentTime)
        }
        rule.mainClock.autoAdvance = false
        try {
            rule.mainClock.advanceTimeBy(1_000)
            rule.activityRule.scenario.moveToState(Lifecycle.State.CREATED)
            paused = pollClockTimes.size
            android.util.Log.i("ProxyReviewPhase", "before stopped effect-clock advance")
            rule.mainClock.advanceTimeBy(4_000)
            android.util.Log.i("ProxyReviewPhase", "after stopped effect-clock advance")
            android.os.SystemClock.sleep(2_000)
            assertEquals(paused, pollClockTimes.size)
            rule.runOnUiThread { lifecycle.addObserver(observer) }
            rule.mainClock.autoAdvance = true
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally {
            rule.mainClock.autoAdvance = true
            rule.runOnUiThread { lifecycle.removeObserver(observer) }
        }
        rule.waitUntil(6_000) { pollClockTimes.size > paused }
        assertTrue(resumedAt.get() >= 0L)
        val interval = pollClockTimes[paused] - resumedAt.get()
        assertTrue("New observation must wait a full interval, got $interval", interval >= 1500)
        rule.runOnIdle { assertTrue(sent.isEmpty()); assertTrue(model.state.installationVerified) }
    }

}
