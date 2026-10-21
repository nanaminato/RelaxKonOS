package app.relaxkonos.mobile.ui.servercenter

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.*
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.*
import org.junit.Assert.*

@OptIn(ExperimentalCoroutinesApi::class)
class ServerMaintenanceViewModelTest {
    private val originalHostId = ServerHostTargetRules.hostId("original.invalid", 22, "review")
    private val otherHostId = ServerHostTargetRules.hostId("other.invalid", 22, "review")
    private val fingerprint = ServerCenterHostKeyObservation("original.invalid", 22, "ssh-ed25519", byteArrayOf(1, 2, 3)).fingerprint
    private var journalBytes: ByteArray? = null
    private var failJournalRead = false
    private var failJournalWrite = false
    private val journalStorage = object : MaintenancePendingStorage {
        override fun read(): ByteArray? { if (failJournalRead) throw java.io.IOException("private journal read"); return journalBytes?.copyOf() }
        override fun write(bytes: ByteArray) { if (failJournalWrite) throw java.io.IOException("private journal write"); journalBytes = bytes.copyOf() }
    }
    private val store = ViewModelStore()
    @Before fun setup() { Dispatchers.setMain(StandardTestDispatcher()) }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private fun model(actions: MaintenanceActions) = ServerMaintenanceViewModel(actions, MaintenancePendingJournal(journalStorage), StandardTestDispatcher()).also { store.put("maintenance", it) }
    private val snapshot = ServerHostSnapshot(installed = true, verifiedAtUtc = "review-time", healthy = true,
        installationId = "11111111-1111-1111-1111-111111111111", version = "1.0", mode = ServerInstallMode.LinuxUser, listenUrl = "https://127.0.0.1:5000")
    private fun TestScope.reviewModel(): Pair<ServerMaintenanceViewModel, MutableList<MaintenanceRequest>> {
        val requests = mutableListOf<MaintenanceRequest>()
        val model = model(MaintenanceActions { request, _, _ -> requests += request; MaintenanceState(snapshot = snapshot) })
        model.check(originalHostId); runCurrent(); model.setReviewActive(true); return model to requests
    }
    private fun pending(request: MaintenanceRequest) = MaintenancePending(ServerInstallOperationReference(request.hostId,
        "ssh-ed25519", fingerprint, "22222222-2222-2222-2222-222222222222", ServerHostPlatform.Linux, 1), request.kind)
    private fun receipt(pending: MaintenancePending, state: ServerDeploymentState = ServerDeploymentState.Failed) = ServerDeploymentOperation(
        ServerDeploymentProtocol.VERSION, pending.reference.operationId, snapshot.installationId, pending.kind,
        ServerDeploymentPhase.Failed, state, 1, "review-time", false)
    @Test fun `only matching terminal original receipt clears pending and never resends mutation`() = runTest {
        var mutations = 0; var recoveryCase = 0
        val model = model(MaintenanceActions { request, _, started ->
            request.recovery?.let { recovery ->
                val result = when (recoveryCase) {
                    0 -> receipt(recovery).copy(operationId = otherHostId)
                    1 -> receipt(recovery).copy(kind = ServerDeploymentKind.Uninstall)
                    2 -> receipt(recovery, ServerDeploymentState.Running)
                    3 -> receipt(recovery).copy(schemaVersion = ServerDeploymentProtocol.VERSION - 1)
                    else -> receipt(recovery)
                }
                return@MaintenanceActions MaintenanceState(recovered = result)
            }
            if (request.kind == ServerDeploymentKind.Status) MaintenanceState(snapshot = snapshot)
            else { mutations++; started(pending(request)); throw CancellationException("private lost receipt") }
        })
        model.check(originalHostId); runCurrent(); model.setReviewActive(true)
        model.requestReview(originalHostId, ServerDeploymentKind.Repair); model.submitReview(requireNotNull(model.state.value.review)); runCurrent()
        repeat(4) { index ->
            recoveryCase = index; model.recover(requireNotNull(model.state.value.pending)); runCurrent()
            assertNotNull(model.state.value.pending); assertNull(model.state.value.snapshot); assertEquals(1, mutations)
        }
        recoveryCase = 4; model.recover(requireNotNull(model.state.value.pending)); runCurrent()
        assertNull(model.state.value.pending); assertNotNull(model.state.value.recovered); assertNull(model.state.value.snapshot)
        model.check(originalHostId); runCurrent(); model.requestReview(originalHostId, ServerDeploymentKind.Repair)
        assertNotNull(model.state.value.review); assertEquals(1, mutations)
    }
    @Test fun `failed recovery retains original reference across another host status and blocks all writes`() = runTest {
        var mutations = 0
        val model = model(MaintenanceActions { request, _, started ->
            if (request.recovery != null) throw CancellationException("private recovery failure")
            if (request.kind == ServerDeploymentKind.Status) MaintenanceState(snapshot = snapshot)
            else { mutations++; started(pending(request)); throw CancellationException("private lost receipt") }
        })
        model.check(originalHostId); runCurrent(); model.setReviewActive(true)
        model.requestReview(originalHostId, ServerDeploymentKind.Repair); model.submitReview(requireNotNull(model.state.value.review)); runCurrent()
        val original = requireNotNull(model.state.value.pending)
        model.recover(requireNotNull(model.state.value.pending)); runCurrent(); assertSame(original, model.state.value.pending)
        model.check(otherHostId); runCurrent(); assertNull(model.state.value.pending)
        model.check(originalHostId); runCurrent(); assertSame(original, model.state.value.pending)
        model.openConfiguration(originalHostId, false); model.firewall(originalHostId, false, ""); model.requestReview(originalHostId, ServerDeploymentKind.Uninstall); runCurrent()
        assertNull(model.state.value.configuration); assertNull(model.state.value.review); assertEquals(1, mutations)
    }
    @Test fun `journal admission failure stops dispatch before mutation counter advances`() = runTest {
        var mutations = 0
        val model = model(MaintenanceActions { request, _, started ->
            if (request.kind == ServerDeploymentKind.Status) MaintenanceState(snapshot = snapshot)
            else { started(pending(request)); mutations++; MaintenanceState(complete = true) }
        })
        model.check(originalHostId); runCurrent(); model.setReviewActive(true)
        model.requestReview(originalHostId, ServerDeploymentKind.Repair)
        failJournalWrite = true; model.submitReview(requireNotNull(model.state.value.review)); runCurrent()
        assertEquals(0, mutations); assertFalse(model.state.value.busy); assertNull(model.state.value.snapshot)
        assertNotNull(model.state.value.error)
    }
    @Test fun `unavailable journal never enters status executor or exposes writable facts`() = runTest {
        var calls = 0
        val model = model(MaintenanceActions { _, _, _ -> calls++; MaintenanceState(snapshot = snapshot) })
        failJournalRead = true; model.check(originalHostId); runCurrent()
        assertEquals(0, calls); assertNull(model.state.value.snapshot); assertFalse(model.state.value.busy)
        failJournalRead = false; model.check(originalHostId); runCurrent(); assertEquals(1, calls)
    }
    @Test fun `failed persisted resolution keeps original gate until storage can commit`() = runTest {
        var mutations = 0
        val model = model(MaintenanceActions { request, _, started ->
            request.recovery?.let { return@MaintenanceActions MaintenanceState(recovered = receipt(it)) }
            if (request.kind == ServerDeploymentKind.Status) MaintenanceState(snapshot = snapshot)
            else { started(pending(request)); mutations++; throw CancellationException("private lost receipt") }
        })
        model.check(originalHostId); runCurrent(); model.setReviewActive(true)
        model.requestReview(originalHostId, ServerDeploymentKind.Repair); model.submitReview(requireNotNull(model.state.value.review)); runCurrent()
        val original = requireNotNull(model.state.value.pending)
        failJournalWrite = true; model.recover(original); runCurrent()
        assertSame(original, model.state.value.pending); assertNotNull(model.state.value.error)
        failJournalWrite = false; model.recover(original); runCurrent(); assertNull(model.state.value.pending)
        val reopened = model(MaintenanceActions { _, _, _ -> MaintenanceState(snapshot = snapshot) })
        reopened.check(originalHostId); runCurrent(); assertNull(reopened.state.value.pending); assertEquals(1, mutations)
    }
    @Test fun `new maintenance model keeps unknown operation gate after previous model is cleared`() = runTest {
        var mutations = 0
        val actions = MaintenanceActions { request, _, started ->
            if (request.kind == ServerDeploymentKind.Status) MaintenanceState(snapshot = snapshot)
            else { mutations++; started(pending(request)); throw CancellationException("private lost receipt") }
        }
        val first = model(actions)
        first.check(originalHostId); runCurrent(); first.setReviewActive(true)
        first.requestReview(originalHostId, ServerDeploymentKind.Repair); first.submitReview(requireNotNull(first.state.value.review)); runCurrent()
        val second = model(actions)
        second.check(originalHostId); runCurrent(); second.setReviewActive(true)
        second.requestReview(originalHostId, ServerDeploymentKind.Repair)
        assertNull(second.state.value.review); assertNotNull(second.state.value.pending); assertEquals(1, mutations)
    }
    @Test fun `old recovery callback cannot verify a later pending instance`() = runTest {
        var recoveries = 0
        val model = model(MaintenanceActions { request, _, started ->
            request.recovery?.let { recoveries++; return@MaintenanceActions MaintenanceState(recovered = receipt(it)) }
            if (request.kind == ServerDeploymentKind.Status) MaintenanceState(snapshot = snapshot)
            else { started(pending(request)); throw CancellationException("private lost receipt") }
        })
        model.check(originalHostId); runCurrent(); model.setReviewActive(true)
        model.requestReview(originalHostId, ServerDeploymentKind.Repair); model.submitReview(requireNotNull(model.state.value.review)); runCurrent()
        val old = requireNotNull(model.state.value.pending)
        model.recover(old); runCurrent(); model.check(originalHostId); runCurrent()
        model.requestReview(originalHostId, ServerDeploymentKind.Repair); model.submitReview(requireNotNull(model.state.value.review)); runCurrent()
        val current = requireNotNull(model.state.value.pending)
        assertNotSame(old, current)
        model.recover(old); runCurrent(); assertSame(current, model.state.value.pending); assertEquals(1, recoveries)
        model.recover(current); runCurrent(); assertNull(model.state.value.pending); assertEquals(2, recoveries)
    }
    @Test fun `authoritative failed receipt does not become unknown or replay mutation`() = runTest {
        var mutations = 0
        val model = model(MaintenanceActions { request, _, started ->
            if (request.kind == ServerDeploymentKind.Status) MaintenanceState(snapshot = snapshot)
            else {
                mutations++; val pending = pending(request); started(pending)
                MaintenanceState(error = app.relaxkonos.mobile.ui.common.UiMessage(R.string.server_maintenance_failed_code), verifiedMutation = receipt(pending))
            }
        })
        model.check(originalHostId); runCurrent(); model.setReviewActive(true)
        model.requestReview(originalHostId, ServerDeploymentKind.Repair); model.submitReview(requireNotNull(model.state.value.review)); runCurrent()
        assertNull(model.state.value.pending); assertNotNull(model.state.value.error)
        model.check(originalHostId); runCurrent(); assertEquals(1, mutations)
        model.requestReview(originalHostId, ServerDeploymentKind.Repair); assertNotNull(model.state.value.review)
    }
    @Test fun `successful status cannot unlock another repair after unknown mutation`() = runTest {
        var mutations = 0
        val model = model(MaintenanceActions { request, _, started ->
            if (request.kind == ServerDeploymentKind.Status) MaintenanceState(snapshot = snapshot)
            else { mutations++; started(MaintenancePending(ServerInstallOperationReference(request.hostId, "ssh-ed25519", fingerprint,
                    "22222222-2222-2222-2222-222222222222", ServerHostPlatform.Linux, 1), request.kind)); throw CancellationException("private lost receipt") }
        })
        model.check(originalHostId); runCurrent(); model.setReviewActive(true)
        model.requestReview(originalHostId, ServerDeploymentKind.Repair)
        model.submitReview(requireNotNull(model.state.value.review)); runCurrent()
        model.check(originalHostId); runCurrent()
        model.requestReview(originalHostId, ServerDeploymentKind.Repair)
        assertNull(model.state.value.review); assertEquals(1, mutations)
    }
    @Test fun `configuration fields retain invalid text and explicit choices when callbacks are invalidated`() = runTest {
        val (model, requests) = reviewModel(); model.openConfiguration(originalHostId, false)
        val editor = requireNotNull(model.state.value.configuration); val fields = editor.fields
        fields.port.value = "invalid port"; fields.roots.value = " /first\n\n /second "
        fields.lan.value = true; fields.guardian.value = true; fields.autoStart.value = true
        fields.access.value = "whitelist"; fields.discard.value = true
        model.setReviewActive(false); model.setReviewActive(true); model.invalidateConfiguration()
        assertSame(fields, model.state.value.configuration?.fields)
        assertEquals("invalid port", fields.port.value); assertEquals(" /first\n\n /second ", fields.roots.value)
        assertTrue(fields.lan.value); assertTrue(fields.guardian.value); assertTrue(fields.autoStart.value)
        assertEquals("whitelist", fields.access.value); assertFalse(fields.discard.value)
        assertTrue(requests.all { it.kind == ServerDeploymentKind.Status })
    }
    @Test fun `explicitly discarded configuration opens fresh fields rather than reviving previous draft`() = runTest {
        val (model, _) = reviewModel(); model.openConfiguration(originalHostId, false)
        val old = requireNotNull(model.state.value.configuration); old.fields.port.value = "6000"
        assertTrue(model.dismissConfiguration(old, model.state.value.configurationVersion))
        model.openConfiguration(originalHostId, false)
        val fresh = requireNotNull(model.state.value.configuration)
        assertNotSame(old.fields, fresh.fields); assertEquals("5000", fresh.fields.port.value)
        assertFalse(fresh.fields.guardian.value)
    }
    @Test fun `paused configuration retains editor and rejects old save and close while fresh save is single use`() = runTest {
        val (model, requests) = reviewModel()
        model.openConfiguration(originalHostId, false)
        val editor = requireNotNull(model.state.value.configuration); val oldVersion = model.state.value.configurationVersion
        val draft = ServerConfigurationDraft(snapshot, 6000, false, false)
        model.setReviewActive(false)
        assertSame(editor, model.state.value.configuration)
        assertFalse(model.configure(editor, oldVersion, draft, ""))
        model.setReviewActive(true)
        assertFalse(model.configure(editor, oldVersion, draft, "")); assertFalse(model.dismissConfiguration(editor, oldVersion))
        val current = model.state.value.configurationVersion
        assertTrue(model.configure(editor, current, draft, "")); runCurrent()
        assertFalse(model.configure(editor, current, draft, "")); runCurrent()
        assertEquals(1, requests.count { it.kind == ServerDeploymentKind.Configure })
        assertSame(draft, requests.last().configuration)
    }
    @Test fun `closed configuration cannot borrow reopened editor with identical snapshot`() = runTest {
        val (model, requests) = reviewModel()
        model.openConfiguration(originalHostId, true)
        val old = requireNotNull(model.state.value.configuration); val version = model.state.value.configurationVersion
        assertTrue(model.dismissConfiguration(old, version)); model.openConfiguration(originalHostId, true)
        val fresh = requireNotNull(model.state.value.configuration)
        assertFalse(model.dismissConfiguration(old, version))
        assertFalse(model.configure(old, version, ServerConfigurationDraft(snapshot, 5000, false, true), ""))
        assertSame(fresh, model.state.value.configuration); assertTrue(requests.all { it.kind == ServerDeploymentKind.Status })
    }
    @Test fun `stale underlying actions cannot discard or mutate open configuration`() = runTest {
        val (model, requests) = reviewModel()
        model.openConfiguration(originalHostId, false); val editor = model.state.value.configuration
        model.check(originalHostId); model.firewall(originalHostId, false, ""); model.requestReview(originalHostId, ServerDeploymentKind.Uninstall)
        model.openConfiguration(originalHostId, true); runCurrent()
        assertSame(editor, model.state.value.configuration); assertNull(model.state.value.review)
        assertTrue(requests.all { it.kind == ServerDeploymentKind.Status })
    }
    @Test fun `configuration cannot substitute another reviewed baseline`() = runTest {
        val (model, requests) = reviewModel()
        model.openConfiguration(originalHostId, false); val editor = requireNotNull(model.state.value.configuration)
        assertFalse(model.configure(editor, model.state.value.configurationVersion,
            ServerConfigurationDraft(snapshot.copy(version = otherHostId), 5000, false, false), ""))
        assertSame(editor, model.state.value.configuration); assertTrue(requests.all { it.kind == ServerDeploymentKind.Status })
    }
    @Test fun `paused page rejects opening and consuming maintenance confirmation`() = runTest {
        val (model, requests) = reviewModel()
        model.requestReview(originalHostId, ServerDeploymentKind.Repair); val review = requireNotNull(model.state.value.review)
        model.setReviewActive(false); model.requestReview(originalHostId, ServerDeploymentKind.Repair)
        assertNull(model.state.value.review); assertFalse(model.submitReview(review))
        model.setReviewActive(true); assertFalse(model.submitReview(review)); runCurrent()
        assertTrue(requests.all { it.kind == ServerDeploymentKind.Status })
    }
    @Test fun `dismissed confirmation cannot borrow newly opened approval`() = runTest {
        val (model, requests) = reviewModel()
        model.requestReview(originalHostId, ServerDeploymentKind.Repair)
        val old = requireNotNull(model.state.value.review); model.dismissReview(old)
        model.requestReview(originalHostId, ServerDeploymentKind.Repair)
        val fresh = requireNotNull(model.state.value.review)
        assertFalse(model.dismissReview(old)); assertSame(fresh, model.state.value.review)
        model.submitReview(old); runCurrent(); assertEquals(1, requests.size)
        model.submitReview(fresh); runCurrent(); assertEquals(2, requests.size)
        assertSame(snapshot, requests.last().reviewedSnapshot); assertEquals(originalHostId, requests.last().hostId)
    }
    @Test fun `confirmation is consumed before launch and remains consumed after completion`() = runTest {
        val (model, requests) = reviewModel()
        model.requestReview(originalHostId, ServerDeploymentKind.Uninstall)
        val review = requireNotNull(model.state.value.review)
        model.submitReview(review); model.submitReview(review); runCurrent(); model.submitReview(review); runCurrent()
        assertEquals(1, requests.count { it.kind == ServerDeploymentKind.Uninstall }); assertNull(model.state.value.review)
    }
    @Test fun `explicit status refresh invalidates original confirmation`() = runTest {
        val (model, requests) = reviewModel()
        model.requestReview(originalHostId, ServerDeploymentKind.Repair); val review = requireNotNull(model.state.value.review)
        model.check(originalHostId); runCurrent(); model.submitReview(review); runCurrent()
        assertTrue(requests.all { it.kind == ServerDeploymentKind.Status }); assertNull(model.state.value.review)
    }
    @Test fun `another host cannot approve current snapshot and old host review cannot submit after switch`() = runTest {
        val (model, requests) = reviewModel()
        model.requestReview(otherHostId, ServerDeploymentKind.Uninstall); assertNull(model.state.value.review)
        model.requestReview(originalHostId, ServerDeploymentKind.Uninstall); val review = requireNotNull(model.state.value.review)
        model.check(otherHostId); runCurrent(); model.submitReview(review); runCurrent()
        assertTrue(requests.all { it.kind == ServerDeploymentKind.Status }); assertEquals(otherHostId, model.state.value.hostId)
    }
    @Test fun `repeated immediate failures publish distinct feedback events`() = runTest {
        val model = model(MaintenanceActions { _, _, _ -> throw CancellationException("private cancellation") })
        model.check(originalHostId); runCurrent(); val first = model.state.value.feedbackVersion
        model.check(originalHostId); runCurrent()
        assertTrue(model.state.value.feedbackVersion > first); assertFalse(model.state.value.busy)
    }
    @Test fun `cancelled status read ends busy and allows explicit retry`() = runTest {
        var calls = 0
        val model = model(MaintenanceActions { _, _, _ ->
            if (++calls == 1) throw CancellationException("private cancellation")
            MaintenanceState()
        })
        model.check(originalHostId); runCurrent()
        assertFalse(model.state.value.busy)
        assertNull(model.state.value.snapshot)
        assertEquals(R.string.server_maintenance_read_failed, model.state.value.error?.resId)
        model.check(originalHostId); runCurrent(); assertEquals(2, calls); assertNull(model.state.value.error)
    }
    @Test fun `cancelled mutation ends busy and reports unknown rather than success`() = runTest {
        val model = model(MaintenanceActions { request, _, started ->
            if (request.kind == ServerDeploymentKind.Status) MaintenanceState(snapshot = snapshot)
            else { started(MaintenancePending(ServerInstallOperationReference(request.hostId, "ssh-ed25519", fingerprint,
                    "22222222-2222-2222-2222-222222222222", ServerHostPlatform.Linux, 1), request.kind)); throw CancellationException("private cancellation") }
        })
        model.check(originalHostId); runCurrent(); model.setReviewActive(true)
        model.requestReview(originalHostId, ServerDeploymentKind.Repair)
        model.submitReview(requireNotNull(model.state.value.review)); runCurrent()
        assertFalse(model.state.value.busy); assertFalse(model.state.value.complete)
        assertNull(model.state.value.snapshot); assertNull(model.state.value.probe)
        assertEquals(R.string.server_maintenance_result_unknown, model.state.value.error?.resId)
        assertTrue(requireNotNull(model.state.value.error).args.isEmpty())
    }
    @Test fun `unexpected setup failure becomes safe feedback and restores busy`() = runTest {
        val model = model(MaintenanceActions { _, _, _ -> throw IllegalStateException("private credential storage detail") })
        model.check(originalHostId); runCurrent()
        assertFalse(model.state.value.busy)
        assertEquals(R.string.server_maintenance_read_failed, model.state.value.error?.resId)
        assertNull(model.state.value.error?.debugDetail)
    }
    @Test fun `suspended maintenance keeps original target and prevents duplicate calls`() = runTest {
        val gate = CompletableDeferred<Unit>(); val targets = mutableListOf<String>()
        val model = model(MaintenanceActions { request, progress, _ ->
            targets += request.hostId; progress(R.string.server_progress_verifying); gate.await(); MaintenanceState()
        })
        model.check(originalHostId); runCurrent(); model.check(otherHostId); runCurrent()
        assertTrue(model.state.value.busy); assertEquals(listOf(originalHostId), targets)
        assertEquals(R.string.server_progress_verifying, model.state.value.progress)
        gate.complete(Unit); runCurrent(); assertFalse(model.state.value.busy)
    }
}
