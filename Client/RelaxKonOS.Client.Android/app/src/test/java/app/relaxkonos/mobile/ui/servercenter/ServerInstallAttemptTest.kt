package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.*
import org.junit.Assert.*
import org.junit.Test

class ServerInstallAttemptTest {
    private val installationId = "rki-" + "a".repeat(32)
    private val receipt = ServerDeploymentOperation(ServerDeploymentProtocol.VERSION,
        "11111111-1111-1111-1111-111111111111", installationId, ServerDeploymentKind.Install,
        ServerDeploymentPhase.Completed, ServerDeploymentState.Succeeded, 1L, "2026-10-08T00:00:00Z", false,
        result = ServerDeploymentResult(installationId, ServerInstallMode.LinuxSystem, "0.3.0", healthy = true))
    private val snapshot = ServerHostSnapshot(true, "2026-10-08T00:00:01Z", installationId,
        ServerInstallMode.LinuxSystem, "0.3.0", healthy = true)

    @Test fun healthySnapshotMustBelongToTheInstallationAndModeFromTheOriginalReceipt() {
        assertTrue(installationSnapshotMatchesReceipt(receipt, snapshot, ServerInstallMode.LinuxSystem))
        assertTrue(installationSnapshotMatchesReceipt(receipt.copy(kind = ServerDeploymentKind.Upgrade), snapshot, ServerInstallMode.LinuxSystem))
        listOf(snapshot.copy(installationId = "rki-" + "b".repeat(32)), snapshot.copy(installationId = null),
            snapshot.copy(mode = ServerInstallMode.LinuxUser), snapshot.copy(installed = false), snapshot.copy(healthy = false)).forEach {
            assertFalse(installationSnapshotMatchesReceipt(receipt, it, ServerInstallMode.LinuxSystem))
        }
    }

    @Test fun missingOrConflictingReceiptIdentityCannotReleaseTheVerificationGate() {
        listOf(receipt.copy(result = null), receipt.copy(installationId = "rki-" + "b".repeat(32)),
            receipt.copy(result = receipt.result!!.copy(installationId = "invalid")),
            receipt.copy(result = receipt.result!!.copy(mode = ServerInstallMode.LinuxUser)),
            receipt.copy(result = receipt.result!!.copy(healthy = false)),
            receipt.copy(state = ServerDeploymentState.Failed), receipt.copy(kind = ServerDeploymentKind.Status)).forEach {
            assertFalse(installationSnapshotMatchesReceipt(it, snapshot, ServerInstallMode.LinuxSystem))
        }
    }
    @Test fun preparationFailureCanBeCorrectedButSentWithoutReceiptCannotBeRepeated() {
        val attempt = ServerInstallAttempt()
        assertTrue(attempt.failure().canSubmit)
        assertFalse(attempt.failure().needsVerification)
        attempt.started = true
        val unknown = attempt.failure()
        assertFalse(unknown.canSubmit)
        assertTrue(unknown.uncertain)
        assertTrue(unknown.needsVerification)
        assertEquals(R.string.server_install_result_unknown, unknown.message)
        assertFalse(unknown.copy(busy = true).canSubmit)
    }

    @Test fun knownSuccessRemainsSuccessWhenHealthReadOrLocalJournalFails() {
        val attempt = ServerInstallAttempt().apply { started = true; receiptState = ServerDeploymentState.Succeeded }
        val unverified = attempt.failure()
        assertTrue(unverified.installed)
        assertTrue(unverified.needsVerification)
        assertFalse(unverified.uncertain)
        assertFalse(unverified.canSubmit)
        assertEquals(R.string.server_install_status_unverified, unverified.message)
    }

    @Test fun runningReceiptCannotBeReplayedAndTerminalRejectionAllowsExplicitCorrection() {
        val attempt = ServerInstallAttempt().apply { started = true }
        listOf(ServerDeploymentState.Queued, ServerDeploymentState.Running).forEach {
            attempt.receiptState = it
            assertTrue(attempt.failure().uncertain)
            assertFalse(attempt.failure().canSubmit)
        }
        attempt.receiptState = ServerDeploymentState.Failed
        val rejection = attempt.failure(sudoRejected = true)
        assertTrue(rejection.canSubmit)
        assertFalse(rejection.needsVerification)
        assertEquals(R.string.ssh_workspace_deploy_sudo_failed, rejection.message)
    }
}
