package app.relaxkonos.mobile.testing

import android.os.Bundle
import android.os.Process
import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.ViewModelStore
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.*
import app.relaxkonos.mobile.ui.servercenter.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import org.junit.*
import org.junit.Assert.*
import java.io.File
import java.io.FileOutputStream

/** Invoke only through tools/Run-MaintenanceProcessReview.ps1; preparation is intentionally killed, never a green test. */
class MaintenanceProcessRecoveryTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val directory get() = File(context.noBackupFilesDir, "maintenance-process-review-v1")
    private val writerFile get() = File(directory, "writer-pid.txt")
    private val host = ServerHostTargetRules.create("process-review.invalid", 22, "review", "process review", 1)
    private val reference = ServerInstallOperationReference(host.hostId, "ssh-ed25519",
        ServerCenterHostKeyObservation(host.sshHost, 22, "ssh-ed25519", byteArrayOf(7, 9, 2)).fingerprint,
        "33333333-3333-3333-3333-333333333333", ServerHostPlatform.Linux, 1)
    private val original = MaintenancePending(reference, ServerDeploymentKind.Repair)
    private val snapshot = ServerHostSnapshot(true, "review-time", installationId = "11111111-1111-1111-1111-111111111111",
        mode = ServerInstallMode.LinuxUser, listenUrl = "https://127.0.0.1:5000", healthy = true, guardianEnabled = false)
    private val store = ViewModelStore()
    private var mutations = 0
    private var recoveries = 0
    private var cleanupAfterSuccess = false
    private lateinit var model: ServerMaintenanceViewModel
    private fun text(id: Int) = context.getString(id)
    private fun journal() = MaintenancePendingJournal(FileMaintenancePendingStorage(directory))
    private fun show(allowOneMutation: Boolean) {
        rule.runOnUiThread {
            model = ServerMaintenanceViewModel(MaintenanceActions { request, _, admit ->
                request.recovery?.let {
                    assertEquals(original, it); recoveries++
                    return@MaintenanceActions MaintenanceState(recovered = ServerDeploymentOperation(ServerDeploymentProtocol.VERSION,
                        reference.operationId, snapshot.installationId, original.kind, ServerDeploymentPhase.Failed,
                        ServerDeploymentState.Failed, 1, "review-time", false))
                }
                if (request.kind == ServerDeploymentKind.Status) MaintenanceState(snapshot = snapshot)
                else {
                    assertTrue("Unexpected mutation in recovery process", allowOneMutation)
                    assertEquals(ServerDeploymentKind.Repair, request.kind)
                    admit(original); mutations++
                    throw CancellationException("Synthetic lost maintenance receipt")
                }
            }, journal(), Dispatchers.Main.immediate)
            store.put("process-maintenance", model)
        }
        rule.setContent { MaterialTheme {
            Box(Modifier.safeDrawingPadding().fillMaxSize()) { ServerMaintenanceContent(host, model) }
        } }
        rule.waitUntil(10_000) { model.state.value.snapshot != null && !model.state.value.busy }
        rule.onNode(hasText(text(R.string.server_maintenance_actions)) and
            SemanticsMatcher.expectValue(androidx.compose.ui.semantics.SemanticsProperties.Role, androidx.compose.ui.semantics.Role.Tab))
            .performScrollTo().performClick()
    }
    @After fun cleanup() {
        rule.runOnUiThread { store.clear() }
        if (cleanupAfterSuccess) {
            listOf("server-maintenance-pending.bin", "server-maintenance-pending.bin.tmp", "writer-pid.txt").forEach {
                val file = File(directory, it); check(!file.exists() || file.delete())
            }
            check(directory.delete())
        }
    }
    @Test fun prepareAndAwaitProcessDeath() {
        assertFalse("A previous manual fixture exists; inspect or recover it before preparing again", directory.exists())
        show(allowOneMutation = true)
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.server_maintenance_repair)) and hasClickAction() and hasAnyAncestor(isDialog())).performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_result_unknown)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        InstrumentationRegistry.getInstrumentation().uiAutomation.waitForIdle(500, 5_000)
        rule.onNodeWithText(text(R.string.server_maintenance_pending)).performScrollTo().assertIsDisplayed()
        assertEquals(original, journal().load(host.hostId)); assertEquals(1, mutations)
        FileOutputStream(writerFile).use { output -> output.write(Process.myPid().toString().toByteArray()); output.fd.sync() }
        // No finally block removes the unresolved record: the driver validates this PID and force-stops this live process.
        while (true) Thread.sleep(1000)
    }
    @Test fun recoverInNewProcessWithoutReplaying() {
        assertTrue(writerFile.isFile)
        val writerPid = writerFile.readText().toInt()
        assertNotEquals("Recovery must run in a different OS process", writerPid, Process.myPid())
        assertEquals(original, journal().load(host.hostId))
        show(allowOneMutation = false)
        rule.onNodeWithText(reference.operationId).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.installation_kind_upgrade)).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.server_maintenance_reconcile)).performScrollTo().performClick()
        rule.waitUntil(10_000) { model.state.value.pending == null && !model.state.value.busy }
        rule.onNodeWithText(text(R.string.server_maintenance_reconciled)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.server_maintenance_check)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.server_maintenance_repair)).performScrollTo().assertIsEnabled()
        assertNull(journal().load(host.hostId)); assertEquals(0, mutations); assertEquals(1, recoveries)
        InstrumentationRegistry.getInstrumentation().sendStatus(2, Bundle().apply {
            putString("maintenanceWriterPid", writerPid.toString()); putString("maintenanceRecoveryPid", Process.myPid().toString())
        })
        cleanupAfterSuccess = true
    }
}
