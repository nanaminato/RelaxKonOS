package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.*
import java.io.File
import java.util.UUID
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ServerInstallRestoreTest {
    @get:Rule val rule = createComposeRule()
    private val app get() = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
    private fun pending() = PendingServerInstall(
        ServerInstallOperationReference(ServerHostTargetRules.create("test-${UUID.randomUUID()}.invalid", 22, "test", null, 1L).hostId,
            "ssh-ed25519", ServerCenterHostKeyObservation("test.invalid", 22, "ssh-ed25519", byteArrayOf(1, 2, 3)).fingerprint,
            UUID.randomUUID().toString(), ServerHostPlatform.Linux, 1L), ServerInstallMode.LinuxSystem,
        ServerDeploymentKind.Install, true, attempted = true)

    @Test fun recreatedViewModelRestoresDiskGateAndDoesNotBeginAnotherInstallation() {
        val original = pending()
        val store = app.container.pendingServerInstalls
        lateinit var recreated: ServerInstallViewModel
        store.write(original.copy(attempted = false))
        store.markStarted(original.copy(attempted = false))
        try {
            rule.runOnIdle {
                val first = ServerInstallViewModel(app)
                first.restore(original.reference.hostId)
                assertFalse(first.state.value.canSubmit)
                recreated = ServerInstallViewModel(app)
                recreated.restore(original.reference.hostId)
                assertTrue(recreated.state.value.uncertain)
                assertTrue(recreated.state.value.needsVerification)
                recreated.install(ServerInstallSelection(original.reference.hostId, "official", null, "", "linuxSystem",
                    "loopback", "restricted", "none", "pfx", null, null, "", "", ""))
                assertFalse(recreated.state.value.busy)
                assertFalse(recreated.state.value.canSubmit)
                recreated.verifyOriginal()
            }
            rule.waitForIdle()
            rule.runOnIdle {
                assertFalse(recreated.state.value.canSubmit)
                assertFalse(recreated.state.value.busy)
                assertTrue(recreated.state.value.needsVerification)
            }
            assertEquals(original, store.read(original.reference.hostId))
        } finally { store.clear(original) }
    }

    @Test fun corruptDiskRecordBlocksNewSubmissionAndKeepsRecoveryFeedback() {
        val original = pending()
        val file = File(app.noBackupFilesDir, "pending-server-install-${original.reference.hostId}.bin")
        file.writeBytes(byteArrayOf(1, 2, 3))
        try {
            rule.runOnIdle {
                val model = ServerInstallViewModel(app)
                model.restore(original.reference.hostId)
                assertFalse(model.state.value.canSubmit)
                assertTrue(model.state.value.needsVerification)
                model.verifyOriginal()
                assertFalse(model.state.value.canSubmit)
            }
            assertArrayEquals(byteArrayOf(1, 2, 3), file.readBytes())
        } finally { assertTrue(file.delete()) }
    }

    @Test fun unsentRecordIsClearedOnRecreationWithoutRemoteVerification() {
        val reserved = pending().copy(attempted = false)
        val store = app.container.pendingServerInstalls
        store.write(reserved)
        try {
            rule.runOnIdle {
                val model = ServerInstallViewModel(app)
                model.restore(reserved.reference.hostId)
                assertTrue(model.state.value.canSubmit)
                assertFalse(model.state.value.busy)
                assertFalse(model.state.value.needsVerification)
            }
            assertNull(store.read(reserved.reference.hostId))
        } finally { store.clear(reserved) }
    }
}
