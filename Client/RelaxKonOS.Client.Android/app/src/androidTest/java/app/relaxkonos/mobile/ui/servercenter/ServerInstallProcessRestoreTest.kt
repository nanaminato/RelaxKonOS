package app.relaxkonos.mobile.ui.servercenter

import android.os.Process
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.*
import java.io.File
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test

/** Run seed, force-stop the app, then run verify with the installRestorePhase argument. */
class ServerInstallProcessRestoreTest {
    @get:Rule val rule = createComposeRule()

    @Test fun pendingInstallationSurvivesAnActualProcessRestart() {
        val phase = InstrumentationRegistry.getArguments().getString("installRestorePhase")
        assumeTrue(phase == "seed" || phase == "verify")
        val sent = InstrumentationRegistry.getArguments().getString("installRestoreSent") != "false"
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        val hostId = ServerHostTargetRules.create("process-install-restore-test.invalid", 22, "test-fixture", null, 1L).hostId
        val original = PendingServerInstall(ServerInstallOperationReference(hostId, "ssh-ed25519",
            ServerCenterHostKeyObservation("process-install-restore-test.invalid", 22, "ssh-ed25519", byteArrayOf(1, 2, 3)).fingerprint,
            "cad0f7ba-a325-4f1d-9319-827618b13cb8", ServerHostPlatform.Linux, 1L),
            ServerInstallMode.LinuxSystem, ServerDeploymentKind.Upgrade, true, attempted = sent)
        val store = app.container.pendingServerInstalls
        val processMarker = File(app.noBackupFilesDir, "install-process-restore-test.pid")
        if (phase == "seed") {
            assertNull("Do not overwrite an existing test or user record", store.read(hostId))
            assertFalse(processMarker.exists())
            store.write(original.copy(attempted = false))
            if (sent) store.markStarted(original.copy(attempted = false))
            processMarker.writeText(Process.myPid().toString())
            assertEquals(original, store.read(hostId))
            return
        }
        assertEquals(original, store.read(hostId))
        assertNotEquals("The app must have restarted", processMarker.readText().toInt(), Process.myPid())
        try {
            lateinit var model: ServerInstallViewModel
            rule.runOnIdle {
                model = ServerInstallViewModel(app)
                model.restore(hostId)
                assertEquals(sent, model.state.value.uncertain)
                assertEquals(sent, model.state.value.needsVerification)
                assertEquals(!sent, model.state.value.canSubmit)
                if (sent) model.install(ServerInstallSelection(hostId, "official", null, "", "linuxSystem", "loopback",
                    "restricted", "none", "pfx", null, null, "", "", ""))
            }
            rule.waitForIdle()
            rule.runOnIdle { assertFalse(model.state.value.busy); assertEquals(!sent, model.state.value.canSubmit) }
            assertEquals(if (sent) original else null, store.read(hostId))
        } finally {
            store.clear(original)
            assertTrue(processMarker.delete())
        }
    }
}
