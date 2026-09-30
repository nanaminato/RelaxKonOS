package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class OperationDestinationsTest {
    private fun owner(vararg capabilities: String) = SessionState.Active("server", "https://example.test", "alice", "workspace",
        capabilities.toSet(), "linux", ExecutionEligibility(true, null, true))
    private fun reference(domain: OperationDomain, resource: String = "target") = OperationReference("server", "alice", domain, resource, "original-op", 0)

    @Test fun `first batch installation links exist only for delivered forms and current capability`() {
        val owner = owner(ServerCapabilities.WEB_SERVER, ServerCapabilities.TUNNELS, ServerCapabilities.PROXY, ServerCapabilities.DOCKER)
        assertEquals(OperationDestination.WebServer, OperationDestinations.operation(owner, reference(OperationDomain.Installation, "Nginx"))?.destination)
        assertEquals(OperationDestination.Tunnels, OperationDestinations.installation(owner, "Frp")?.destination)
        assertEquals(OperationTarget(OperationDestination.Proxy), OperationDestinations.installation(owner, "Mihomo"))
        assertEquals(OperationTarget(OperationDestination.DockerControl), OperationDestinations.installation(owner, "Docker"))
        assertNull(OperationDestinations.installation(owner(), "Mihomo"))
        assertNull(OperationDestinations.installation(owner, "unknown"))
    }

    @Test fun `SMB installation links only to delivered form with file services capability`() {
        assertEquals(OperationTarget(OperationDestination.Smb), OperationDestinations.installation(owner(ServerCapabilities.FILE_SERVICES), "Smb"))
        assertNull(OperationDestinations.installation(owner(), "Smb"))
        val other = reference(OperationDomain.Installation, "Smb").copy(account = "bob")
        assertNull(OperationDestinations.operation(owner(ServerCapabilities.FILE_SERVICES), other))
    }

    @Test fun `domain links preserve original operation and hide removed capabilities`() {
        val reference = reference(OperationDomain.Proxy, "lifecycle.start")
        assertEquals(OperationTarget(OperationDestination.Proxy, "original-op"), OperationDestinations.operation(owner(ServerCapabilities.PROXY), reference))
        assertNull(OperationDestinations.operation(owner(), reference))
        assertEquals(OperationTarget(OperationDestination.Certificate, "original-op"),
            OperationDestinations.operation(owner(ServerCapabilities.CERTIFICATES), reference(OperationDomain.Certificate)))
    }

    @Test fun `backup does not link to an unavailable application page`() {
        val reference = reference(OperationDomain.Backup)
        assertNull(OperationDestinations.operation(owner(ServerCapabilities.BACKUP_RECOVERY), reference))
        assertEquals(OperationTarget(OperationDestination.Deployment, "target"),
            OperationDestinations.operation(owner(ServerCapabilities.BACKUP_RECOVERY, ServerCapabilities.APPLICATION_DEPLOYMENTS), reference))
    }

    @Test fun `alert remediation uses fixed supported destinations and rejects arbitrary instructions`() {
        fun alert(kind: String, resource: String? = null, operation: String? = null) =
            OperationalAlert("alert", "type", "error", "open", "code", 1, null, kind, resource, operation)
        val owner = owner(ServerCapabilities.PROXY, ServerCapabilities.CERTIFICATES, ServerCapabilities.GUARDIAN,
            ServerCapabilities.DOCKER, ServerCapabilities.TUNNELS, ServerCapabilities.APPLICATION_DEPLOYMENTS)
        assertEquals(OperationTarget(OperationDestination.Certificate, "original"), OperationDestinations.alert(owner, alert("certificate", "cert", "original")))
        assertEquals(OperationDestination.Guardian, OperationDestinations.alert(owner, alert("guardianWorkload", "workload"))?.destination)
        assertEquals(OperationDestination.Docker, OperationDestinations.alert(owner, alert("dockerOverview"))?.destination)
        assertEquals(OperationDestination.Tunnels, OperationDestinations.alert(owner, alert("tunnelDefinition", "tunnel"))?.destination)
        assertEquals(OperationTarget(OperationDestination.Deployment, "app"), OperationDestinations.alert(owner, alert("applicationDeploymentOperation", "app", "op")))
        assertNull(OperationDestinations.alert(owner, alert("applicationDeployment")))
        assertNull(OperationDestinations.alert(owner(), alert("certificate", "cert")))
        assertNull(OperationDestinations.alert(owner, alert("https://external.test")))
        assertNull(OperationDestinations.alert(owner, alert("eventAlertDetail", "alert")))
    }
}
