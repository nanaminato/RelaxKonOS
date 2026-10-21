package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.servercenter.ServerDeploymentKind
import app.relaxkonos.mobile.servercenter.ServerHostSnapshot
import org.junit.Assert.*
import org.junit.Test

class ServerMaintenanceTest {
    @Test fun `maintenance preflight ignores only verification time and rejects changed installation facts`() {
        val reviewed = ServerHostSnapshot(installed = true, verifiedAtUtc = "first", healthy = true, version = "1.0", installationId = "original")
        assertTrue(maintenanceSnapshotMatches(reviewed.copy(verifiedAtUtc = "second"), reviewed))
        assertFalse(maintenanceSnapshotMatches(reviewed.copy(version = "2.0"), reviewed))
        assertFalse(maintenanceSnapshotMatches(reviewed.copy(installationId = "other"), reviewed))
        assertFalse(maintenanceSnapshotMatches(reviewed.copy(healthy = false), reviewed))
        assertFalse(maintenanceSnapshotMatches(reviewed.copy(installed = false), reviewed))
    }
    @Test fun `successful receipts still require authoritative status postconditions`() {
        val installed = ServerHostSnapshot(installed = true, verifiedAtUtc = "2026-10-01", healthy = true)
        assertFalse(maintenanceResultValid(ServerDeploymentKind.Uninstall, installed))
        assertTrue(maintenanceResultValid(ServerDeploymentKind.Uninstall, installed.copy(installed = false)))
        assertTrue(maintenanceResultValid(ServerDeploymentKind.Repair, installed))
        assertFalse(maintenanceResultValid(ServerDeploymentKind.Repair, installed.copy(healthy = false)))
        assertFalse(maintenanceResultValid(ServerDeploymentKind.Repair, installed.copy(installed = false)))
    }
}
