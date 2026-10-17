package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import androidx.lifecycle.Lifecycle
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.ViewModelStore
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class TunnelReadRecoveryTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val store = ViewModelStore()
    private lateinit var model: TunnelsViewModel
    private lateinit var owner: SessionState.Active
    @Volatile private var frpsReads = 0
    @Volatile private var profileReads = 0
    @Volatile private var installationReads = 0
    private var runningConnection = false
    private var runningInstallation = false
    private var completeInstallationAfterReads: Int? = null
    private var activeInstallationEmpty = false
    private var activeInstallationFailure = false
    private var installationStarts = 0
    private val cancelledInstallationIds = mutableListOf<String>()
    private var lifecycleWrites = 0
    private val queriedInstallationIds = mutableListOf<String>()
    private val profileId = "22222222-2222-2222-2222-222222222222"
    private val profile = TunnelProfile(profileId, "review-edge", "frp.example.test", 7000, TunnelAuth.None,
        false, TunnelTls.Default, TunnelRuntimeMode.Managed, null, 1, 1, 1)
    private val definition = TunnelDefinition("33333333-3333-3333-3333-333333333333", profileId, "review-tcp", "review",
        TunnelProtocol.Tcp, "127.0.0.1", 8080, 7001, null, true, false, false, 1, 1, 1, TunnelConnectionState.Connected, "")
    private val operation = InstallationOperation("44444444-4444-4444-4444-444444444444", InstallationService.Frp,
        InstallationKind.Install, InstallationState.Running, InstallationStage.Downloading, 20, null, 1, 1, null, true)
    private var runningFrps = false
    private var cancelledRead: String? = null
    private var frpsProblem = false
    private val runtime = TunnelRuntime("managed", TunnelRuntimeMode.Managed,
        TunnelRuntimeState.NotInstalled, null, null, "", null, null, false)
    private val frps = ManagedFrps("0.0.0.0", 7000, emptyList(), null, null,
        false, false, false, "127.0.0.1", null, null, false, ManagedFrpsState.NotConfigured,
        0, null, "", null)

    @After fun cleanup() { rule.runOnUiThread { store.clear() } }
    private fun storage() = object : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private fun cancelAt(stage: String) {
        if (cancelledRead == stage) throw CancellationException("isolated read cancellation")
    }
    private fun start() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val app = context.applicationContext as RelaxKonApplication
        val gateway = object : RelaxKonGateway by app.container.gateway {
            override suspend fun login(serverUrl: String, identifier: String, password: CharArray) = ApiResult.Success(
                LoginSession("review", "review", ServerDescriptor("linux", setOf(ServerCapabilities.TUNNELS), true),
                    AuthTokens("test-access", "test-refresh", null, null), workspaceId = "11111111-1111-1111-1111-111111111111"))
            override suspend fun tunnelProfiles(serverUrl: String, accessToken: String): ApiResult<List<TunnelProfile>> {
                profileReads++
                cancelAt("profiles"); return ApiResult.Success(if (runningConnection) listOf(profile) else emptyList())
            }
            override suspend fun tunnelDefinitions(serverUrl: String, accessToken: String): ApiResult<List<TunnelDefinition>> {
                cancelAt("definitions"); return ApiResult.Success(if (runningConnection) listOf(definition) else emptyList())
            }
            override suspend fun tunnelRuntime(serverUrl: String, accessToken: String): ApiResult<TunnelRuntime> {
                cancelAt("runtime"); return ApiResult.Success(runtime)
            }
            override suspend fun managedFrps(serverUrl: String, accessToken: String): ApiResult<ManagedFrps> {
                frpsReads++
                cancelAt("frps")
                return if (frpsProblem) ApiResult.Problem(403, "tunnel.forbidden", null) else ApiResult.Success(
                    if (runningFrps) frps.copy(state = ManagedFrpsState.Running, revision = 1, allowPorts = listOf(TunnelPortRange(7001, 7010))) else frps)
            }
            override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService): ApiResult<InstallationOperation?> =
                if (activeInstallationFailure) ApiResult.Transport(null) else ApiResult.Success(if (runningInstallation && !activeInstallationEmpty) operation else null)
            override suspend fun startInstallation(serverUrl: String, accessToken: String, kind: InstallationKind, request: InstallationRequest, idempotencyKey: String): ApiResult<InstallationOperation> {
                installationStarts++; return ApiResult.Problem(400, "installation.invalid_request", null)
            }
            override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<InstallationOperation> {
                cancelledInstallationIds += operationId
                return ApiResult.Problem(403, "installation.forbidden", null)
            }
            override suspend fun installation(serverUrl: String, accessToken: String, operationId: String): ApiResult<InstallationOperation> {
                queriedInstallationIds += operationId
                installationReads++
                cancelAt("installation")
                return ApiResult.Success(if (completeInstallationAfterReads?.let { installationReads >= it } == true)
                    operation.copy(state = InstallationState.Succeeded, stage = InstallationStage.Completed, progress = 100, completedAtMillis = 2, cancellable = false)
                    else operation)
            }
            override suspend fun stopManagedFrps(serverUrl: String, accessToken: String): ApiResult<TunnelResult> {
                lifecycleWrites++; return ApiResult.Problem(403, "tunnel.forbidden", null)
            }
            override suspend fun stopTunnelProfile(serverUrl: String, accessToken: String, id: String): ApiResult<TunnelResult> {
                lifecycleWrites++; return ApiResult.Problem(403, "tunnel.forbidden", null)
            }
        }
        val session = AuthSession(gateway)
        runBlocking { session.login(ServerConnectionIdentityRules.direct("https://tunnel-review.invalid"), "review", "test".toCharArray()) {} }
        owner = session.state.value as SessionState.Active
        val index = OperationIndex(object : OperationIndexStorage {
            var bytes: ByteArray? = null
            override fun read() = bytes
            override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
        })
        val elevations = ElevationRepository(gateway, session, app.container.vault, UsageMemoryStore(InMemoryUsageMemoryStorage()))
        val repository = TunnelRepository(gateway, session, elevations, TunnelMutationJournal(storage()))
        val installations = InstallationRepository(gateway, session, elevations, index, InstallationRequestJournal(storage()))
        rule.runOnUiThread {
            model = TunnelsViewModel(session, repository, installations, index, ElevationAnswerProvider.Declines, UploadDocumentOpener { null })
            store.put("tunnels", model)
        }
        refresh()
    }
    private fun refresh() {
        rule.runOnUiThread { model.refresh() }
        rule.waitUntil(5_000) { !model.state.busy }
    }

    @Test fun installationCancellationRejectsDismissedAndReopenedReviewsAndSendsOnce() {
        runningInstallation = true
        start()
        rule.runOnIdle {
            model.requestInstallationCancellation(owner, operation)
            val old = requireNotNull(model.installationCancellation)
            model.dismissInstallationCancellation()
            model.requestInstallationCancellation(owner, operation)
            val current = requireNotNull(model.installationCancellation)
            model.cancelInstall(old)
            assertSame(current, model.installationCancellation)
            assertTrue(cancelledInstallationIds.isEmpty())
            model.cancelInstall(current)
            model.cancelInstall(current)
        }
        rule.waitUntil(5_000) { !model.state.busy }
        rule.runOnIdle { assertEquals(listOf(operation.operationId), cancelledInstallationIds) }
    }

    @Test fun installationConfirmationBlocksConnectionPollingAndIsRevokedOnPause() {
        runningInstallation = true
        runningConnection = true
        start(); showPage()
        rule.runOnIdle {
            model.select(profileId)
            model.requestInstallationCancellation(owner, operation)
        }
        rule.waitForIdle()
        val before = profileReads
        val reads = installationReads
        Thread.sleep(3_800)
        rule.runOnIdle {
            assertEquals(before, profileReads)
            assertEquals(reads, installationReads)
            assertNotNull(model.installationCancellation)
        }
        val review = requireNotNull(model.installationCancellation)
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.runOnIdle {
            assertNull(model.installationCancellation)
            model.cancelInstall(review)
            assertTrue(cancelledInstallationIds.isEmpty())
        }
    }

    @Test fun cancelledAggregateReadNeverRestoresPreviousSuccessfulSnapshots() {
        start()
        for (stage in listOf("profiles", "definitions", "runtime", "frps")) {
            rule.runOnIdle { assertTrue(model.state.frps is ApiResult.Success); cancelledRead = stage }
            refresh()
            rule.runOnIdle {
                assertNull(model.state.facts); assertNull(model.state.runtime)
                assertNull(model.state.frps); assertNull(model.state.frpsAtMillis)
                assertFalse(model.state.busy)
                cancelledRead = null
            }
            refresh()
            rule.runOnIdle { assertTrue(model.state.facts is ApiResult.Success); assertTrue(model.state.frps is ApiResult.Success) }
        }
    }

    @Test fun cancelledFrpsReadKeepsDraftButRemovesOldServiceSnapshot() {
        start()
        rule.runOnUiThread { model.editFrps() }
        val draft = model.state.frpsDraft!!.copy(bindPort = "7443")
        rule.runOnUiThread { model.updateFrps(draft); cancelledRead = "frps"; model.observeFrps() }
        rule.waitUntil(5_000) { !model.state.busy }
        rule.runOnIdle {
            assertNull(model.state.frps); assertNull(model.state.frpsAtMillis)
            assertEquals(draft, model.state.frpsDraft)
            cancelledRead = null
        }
        rule.runOnUiThread { model.observeFrps() }
        rule.waitUntil(5_000) { !model.state.busy }
        rule.runOnIdle { assertTrue(model.state.frps is ApiResult.Success); assertEquals(draft, model.state.frpsDraft) }
    }

    @Test fun isolatedFrpsRejectionIsReportedAndExplicitRefreshRecovers() {
        start()
        rule.runOnIdle { frpsProblem = true }
        refresh()
        rule.runOnIdle {
            assertTrue(model.state.facts is ApiResult.Success); assertTrue(model.state.runtime is ApiResult.Success)
            assertEquals("tunnel.forbidden", model.state.problemCode); assertTrue(model.state.uncertain)
            frpsProblem = false
        }
        refresh()
        rule.runOnIdle { assertNull(model.state.problemCode); assertFalse(model.state.uncertain) }
    }

    @Test fun frpsPageStopsAutomaticReadsWhilePausedAndResumesInForeground() {
        runningFrps = true
        start()
        rule.setContent { MaterialTheme { TunnelsContent(model, owner, {}) } }
        rule.waitUntil(5_000) { !model.state.busy }
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        rule.onAllNodesWithText(context.getString(R.string.frps_server_tab)).filter(hasClickAction()).onFirst().performClick()
        val initial = frpsReads
        rule.waitUntil(8_000) { frpsReads > initial && !model.state.busy }
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitForIdle()
        rule.waitUntil(5_000) { !model.state.busy }
        val paused = frpsReads
        Thread.sleep(3_800)
        assertEquals("Paused page must not poll frps", paused, frpsReads)
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.waitUntil(8_000) { frpsReads > paused && !model.state.busy }
    }

    private fun showPage() {
        rule.setContent { MaterialTheme { TunnelsContent(model, owner, {}) } }
        rule.waitUntil(5_000) { !model.state.busy }
    }
    private fun verifyBackgroundScheduling(reads: () -> Int) {
        val initial = reads()
        rule.waitUntil(8_000) { reads() > initial && !model.state.busy }
        rule.runOnUiThread {
            assertFalse("Observation failed: ${model.state.problemCode}, installationVerified=${model.state.installationVerified}", model.state.uncertain)
        }
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitForIdle()
        rule.waitUntil(5_000) { !model.state.busy }
        val paused = reads()
        Thread.sleep(3_800)
        assertEquals("Paused page must not schedule reads", paused, reads())
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        try { rule.waitUntil(8_000) { reads() > paused && !model.state.busy } }
        catch (failure: Throwable) {
            throw AssertionError("Resume reads=$paused -> ${reads()}, busy=${model.state.busy}, uncertain=${model.state.uncertain}, problem=${model.state.problemCode}, installationVerified=${model.state.installationVerified}, operation=${model.state.installation}", failure)
        }
    }
    @Test fun connectionObservationStopsWhilePausedAndResumesInForeground() {
        runningConnection = true
        start(); showPage()
        rule.runOnUiThread { model.select(profileId) }
        verifyBackgroundScheduling { profileReads }
    }
    @Test fun installationObservationStopsWhilePausedAndResumesInForeground() {
        runningInstallation = true
        start(); showPage()
        verifyBackgroundScheduling { installationReads }
    }
    @Test fun identicalRunningInstallationSamplesContinueUntilTerminalState() {
        runningInstallation = true
        completeInstallationAfterReads = 4
        start(); showPage()
        rule.waitUntil(12_000) { model.state.installation?.state == InstallationState.Succeeded && !model.state.busy }
        assertEquals(4, installationReads)
        Thread.sleep(2_000)
        assertEquals("Terminal operation must stop polling", 4, installationReads)
    }
    @Test fun cancelledInstallationReadRevokesVerificationAndExplicitPollRecovers() {
        runningInstallation = true
        start()
        rule.runOnUiThread { cancelledRead = "installation"; model.pollInstall() }
        rule.waitUntil(5_000) { !model.state.busy }
        rule.runOnIdle {
            assertEquals(operation.operationId, model.state.installation?.operationId)
            assertFalse(model.state.installationVerified)
            cancelledRead = null
        }
        rule.runOnUiThread { model.pollInstall() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.installationVerified }
    }
    @Test fun aggregateFailurePausesConnectionObservationUntilExplicitRecovery() {
        runningConnection = true
        start(); showPage()
        rule.runOnUiThread { model.select(profileId); frpsProblem = true }
        rule.waitUntil(8_000) { model.state.problemCode == "tunnel.forbidden" && !model.state.busy }
        val failed = profileReads
        Thread.sleep(3_800)
        assertEquals("Read rejection must remain until explicit recovery", failed, profileReads)
        rule.runOnUiThread { frpsProblem = false; model.refresh() }
        rule.waitUntil(5_000) { !model.state.busy && model.state.problemCode == null && !model.state.uncertain }
        val recovered = profileReads
        rule.waitUntil(8_000) { profileReads > recovered }
    }
    @Test fun activeInstallationFailureRemainsVisibleWithoutLosingOriginalTask() {
        runningInstallation = true
        start()
        rule.runOnUiThread { activeInstallationFailure = true }
        refresh()
        rule.runOnIdle {
            assertTrue(model.state.uncertain)
            assertFalse(model.state.installationVerified)
            assertEquals(operation.operationId, model.state.installation?.operationId)
            activeInstallationFailure = false
            activeInstallationEmpty = true
            completeInstallationAfterReads = 1
        }
        refresh()
        rule.runOnIdle {
            assertEquals(InstallationState.Succeeded, model.state.installation?.state)
            assertTrue(model.state.installationVerified)
            assertEquals(listOf(operation.operationId), queriedInstallationIds)
            assertEquals(0, installationStarts)
        }
    }
    @Test fun recordsPageRefreshRecoversCancelledOriginalTaskWhenActiveListIsEmpty() {
        runningInstallation = true
        start(); showPage()
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        rule.onAllNodesWithText(context.getString(R.string.tunnels_records)).filter(hasClickAction()).onFirst()
            .performScrollTo().performClick().assertIsSelected()
        rule.runOnUiThread { cancelledRead = "installation"; model.pollInstall() }
        rule.waitUntil(8_000) { model.state.uncertain && !model.state.busy && !model.state.installationVerified }
        rule.onNodeWithText(context.getString(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(operation.operationId).assertExists()
        rule.onNodeWithText(context.getString(R.string.tunnels_unknown)).assertExists()
        rule.runOnUiThread {
            cancelledRead = null
            activeInstallationEmpty = true
            completeInstallationAfterReads = installationReads + 1
        }
        rule.onAllNodesWithText(context.getString(R.string.common_refresh)).filter(hasClickAction()).onFirst().performClick()
        rule.waitUntil(5_000) { model.state.installationVerified && !model.state.busy && model.state.installation?.state == InstallationState.Succeeded }
        rule.runOnIdle {
            assertFalse(model.state.uncertain)
            assertTrue(queriedInstallationIds.isNotEmpty())
            assertTrue(queriedInstallationIds.all { it == operation.operationId })
            assertEquals(0, installationStarts)
        }
    }
    @Test fun frpsConfirmationPausesObservationAndIsWithdrawnAfterBackgroundOrRefresh() {
        runningFrps = true
        start(); showPage()
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        fun text(id: Int) = context.getString(id)
        rule.onAllNodesWithText(text(R.string.frps_server_tab)).filter(hasClickAction()).onFirst().performClick()
        rule.onNodeWithText(text(R.string.frps_stop)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.frps_stop_confirm)).assertIsDisplayed()
        rule.waitForIdle()
        val before = frpsReads
        Thread.sleep(3_800)
        assertEquals(before, frpsReads)
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitForIdle()
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.onNodeWithText(text(R.string.frps_stop_confirm)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.frps_stop)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.frps_stop_confirm)).assertIsDisplayed()
        rule.runOnUiThread { model.observeFrps() }
        rule.waitUntil(5_000) { !model.state.busy }
        rule.onNodeWithText(text(R.string.frps_stop_confirm)).assertDoesNotExist()
        rule.runOnIdle { assertEquals(0, lifecycleWrites) }
    }
    @Test fun profileConfirmationPausesObservationAndIsWithdrawnAfterBackgroundOrRefresh() {
        runningConnection = true
        start(); showPage()
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        fun text(id: Int) = context.getString(id)
        rule.onAllNodesWithText(text(R.string.workspace_tunnels)).filter(hasClickAction()).onFirst().performClick()
        rule.runOnUiThread { model.select(profileId) }
        rule.onNodeWithText(text(R.string.tunnels_stop)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.tunnels_stop_confirm)).assertIsDisplayed()
        rule.waitForIdle()
        val before = profileReads
        Thread.sleep(3_800)
        assertEquals(before, profileReads)
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitForIdle()
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.onNodeWithText(text(R.string.tunnels_stop_confirm)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.tunnels_stop)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.tunnels_stop_confirm)).assertIsDisplayed()
        rule.runOnUiThread { model.observe() }
        rule.waitUntil(5_000) { !model.state.busy }
        rule.onNodeWithText(text(R.string.tunnels_stop_confirm)).assertDoesNotExist()
        rule.runOnIdle { assertEquals(0, lifecycleWrites) }
    }
}
