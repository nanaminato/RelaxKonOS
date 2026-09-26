package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ApplicationDeploymentWireTest {
    private val application = """{
        "id":"d3708cc7-3e7e-42ad-b498-11466a48af23", "name":"website",
        "sourceKind":"Image", "workloadKind":"Web", "desiredState":"Running",
        "actualState":"Unknown", "readinessLevel":"Http", "containerPort":8080,
        "hostPort":null, "bindAddress":"127.0.0.1", "currentRevisionNumber":null,
        "containerName":null, "domain":null, "driftProblemCode":null,
        "configuration":[{"name":"TOKEN","value":"must-not-be-retained","isSecret":true}]
    }"""

    @Test fun `routes match the authoritative protocol version and encode only UUIDs`() {
        assertEquals("/api/v1.0/application-deployments/applications", ApplicationDeploymentRoutes.APPLICATIONS)
        assertEquals("/api/v1.0/docker/status", ApplicationDeploymentRoutes.RUNTIME)
        assertTrue(ApplicationDeploymentRoutes.application("d3708cc7-3e7e-42ad-b498-11466a48af23").endsWith("/d3708cc7-3e7e-42ad-b498-11466a48af23"))
        assertThrows(IllegalArgumentException::class.java) { ApplicationDeploymentRoutes.application("../operations") }
    }

    @Test fun `nullable runtime facts stay null and secrets are not retained`() {
        val app = ApplicationDeploymentWire.applications("[$application]").single()
        assertNull(app.hostPort)
        assertNull(app.currentRevisionNumber)
        assertNull(app.containerName)
        assertNull(app.domain)
        assertEquals("Unknown", app.actualState)
        assertFalse(app.toString().contains("must-not-be-retained"))
    }

    @Test fun `missing required runtime state rejects the response`() {
        val json = JSONObject(application).apply { remove("actualState") }
        assertThrows(Exception::class.java) { ApplicationDeploymentWire.applications("[$json]") }
    }

    @Test fun `unfamiliar state is preserved without substituting stopped or running`() {
        val json = JSONObject(application).put("actualState", "FutureState")
        assertEquals("FutureState", ApplicationDeploymentWire.applications("[$json]").single().actualState)
    }

    @Test fun `snapshot reads active operation independently of recent history`() {
        val operation = """{"operationId":"op-1","kind":"Deploy","state":"Running","stage":"Pulling",
            "progress":null,"problemCode":null,"recoveryProblemCode":null,"createdAt":"2026-09-26T12:00:00Z"}"""
        val snapshot = ApplicationDeploymentWire.snapshot("""{"application":$application,
            "revisions":[{"id":"rev-1","number":1,"imageReference":"image@sha256:abc","isCurrent":false}],
            "operations":[],"activeOperation":$operation}""")
        assertTrue(snapshot.operations.isEmpty())
        assertEquals("Pulling", snapshot.activeOperation!!.stage)
        assertNull(snapshot.activeOperation.progress)
        assertNotNull(snapshot.activeOperation.createdAtMillis)
        assertFalse(snapshot.revisions.single().isCurrent)
    }

    @Test fun `runtime missing and runtime available are authoritative booleans`() {
        val missing = ApplicationDeploymentWire.runtime("""{"isAvailable":false,"problemCode":"docker.not_installed","serverVersion":null,"operatingSystem":null,"architecture":null}""")
        assertFalse(missing.isAvailable)
        assertEquals("docker.not_installed", missing.problemCode)
        assertNull(missing.serverVersion)
        assertTrue(ApplicationDeploymentWire.runtime("""{"isAvailable":true,"problemCode":"","serverVersion":"28","operatingSystem":"linux","architecture":"amd64"}""").isAvailable)
        assertThrows(Exception::class.java) { ApplicationDeploymentWire.runtime("""{"problemCode":""}""") }
    }
}
