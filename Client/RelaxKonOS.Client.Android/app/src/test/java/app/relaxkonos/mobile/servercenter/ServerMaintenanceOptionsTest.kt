package app.relaxkonos.mobile.servercenter

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class ServerMaintenanceOptionsTest {
    private val id = "rki-7938f9cecaf40c97a275dada52cbf27f"

    @Test fun `ordinary repair preserves certificate and data`() {
        val options = maintenanceOptions(ServerDeploymentKind.Repair, ServerInstallMode.LinuxSystem, id, purge = true)
        assertNull(options.certificateMode)
        assertNull(options.selfSignedIdentities)
        assertEquals(ServerDataRetention.Retain, options.retention)
        assertEquals(id, options.expectedInstallationId)
    }

    @Test fun `system repair serializes explicit certificate rotation with current IP`() {
        for (mode in listOf(ServerInstallMode.LinuxSystem, ServerInstallMode.WindowsSystem)) {
            val options = maintenanceOptions(ServerDeploymentKind.Repair, mode, id, repairCertificate = true,
                certificateIdentities = " localhost, 127.0.0.1,192.168.1.5,localhost ")
            assertEquals("selfSigned", options.certificateMode)
            assertEquals("localhost,127.0.0.1,192.168.1.5", options.selfSignedIdentities)
            val json = String(ServerDeploymentWire.writeRequest(ServerDeploymentRequest(
                ServerDeploymentProtocol.VERSION, UUID.randomUUID().toString(), ServerDeploymentKind.Repair, options)))
            assertTrue(json.contains("\"certificateMode\":\"selfSigned\""))
            assertTrue(json.contains("\"selfSignedIdentities\":\"localhost,127.0.0.1,192.168.1.5\""))
            assertTrue(options.confirmed)
        }
    }

    @Test fun `user mode and malformed identities cannot rotate certificate`() {
        assertThrows(IllegalArgumentException::class.java) {
            maintenanceOptions(ServerDeploymentKind.Repair, ServerInstallMode.LinuxUser, id,
                repairCertificate = true, certificateIdentities = "192.168.1.5")
        }
        for (value in listOf("", " ", "localhost,", "192.168.1.5; touch /tmp/x", "host\nname")) {
            assertThrows(IllegalArgumentException::class.java) { normalizeRepairCertificateIdentities(value) }
        }
    }

    @Test fun `certificate form cannot affect uninstall or rollback`() {
        for (kind in listOf(ServerDeploymentKind.Uninstall, ServerDeploymentKind.Rollback)) {
            val options = maintenanceOptions(kind, ServerInstallMode.LinuxSystem, id,
                repairCertificate = true, certificateIdentities = "192.168.1.5")
            assertNull(options.certificateMode)
            assertNull(options.selfSignedIdentities)
        }
    }
}
