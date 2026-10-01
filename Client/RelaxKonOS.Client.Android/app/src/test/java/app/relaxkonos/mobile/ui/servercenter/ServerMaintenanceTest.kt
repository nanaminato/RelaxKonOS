package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.servercenter.ServerDeploymentKind
import app.relaxkonos.mobile.servercenter.ServerHostSnapshot
import org.junit.Assert.*
import org.junit.Test

class ServerMaintenanceTest {
    @Test fun `successful receipts still require authoritative status postconditions`() {
        val installed = ServerHostSnapshot(installed = true, verifiedAtUtc = "2026-10-01", healthy = true)
        assertFalse(maintenanceResultValid(ServerDeploymentKind.Uninstall, installed))
        assertTrue(maintenanceResultValid(ServerDeploymentKind.Uninstall, installed.copy(installed = false)))
        assertTrue(maintenanceResultValid(ServerDeploymentKind.Repair, installed))
        assertFalse(maintenanceResultValid(ServerDeploymentKind.Repair, installed.copy(healthy = false)))
        assertFalse(maintenanceResultValid(ServerDeploymentKind.Repair, installed.copy(installed = false)))
    }
}
