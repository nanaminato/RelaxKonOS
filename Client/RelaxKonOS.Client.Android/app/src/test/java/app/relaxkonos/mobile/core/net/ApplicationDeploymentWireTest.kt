package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ApplicationDeploymentWireTest {
    private val application = """{
        "id":"d3708cc7-3e7e-42ad-b498-11466a48af23", "name":"website",
        "sourceKind":"image", "workloadKind":"web", "desiredState":"running",
        "actualState":"unknown", "readinessLevel":"http", "containerPort":8080,
        "hostPort":null, "bindAddress":"127.0.0.1", "currentRevisionNumber":null,
        "containerName":null, "domain":null, "driftProblemCode":null,
        "configuration":[{"name":"TOKEN","value":"must-not-be-retained","isSecret":true}]
    }"""

    @Test fun `routes match the authoritative protocol version and encode only UUIDs`() {
        assertEquals("/api/v1.0/application-deployments/applications", ApplicationDeploymentRoutes.APPLICATIONS)
        assertEquals("/api/v1.0/docker/status", ApplicationDeploymentRoutes.RUNTIME)
        assertTrue(ApplicationDeploymentRoutes.application("d3708cc7-3e7e-42ad-b498-11466a48af23").endsWith("/d3708cc7-3e7e-42ad-b498-11466a48af23"))
        assertTrue(ApplicationDeploymentRoutes.rollback("d3708cc7-3e7e-42ad-b498-11466a48af23").endsWith("/rollback"))
        assertThrows(IllegalArgumentException::class.java) { ApplicationDeploymentRoutes.application("../operations") }
    }

    @Test fun `nullable runtime facts stay null and secrets are not retained`() {
        val app = ApplicationDeploymentWire.applications("[$application]").single()
        assertNull(app.hostPort)
        assertNull(app.currentRevisionNumber)
        assertNull(app.containerName)
        assertNull(app.domain)
        assertEquals("unknown", app.actualState)
        assertFalse(app.toString().contains("must-not-be-retained"))
    }

    @Test fun `missing required runtime state rejects the response`() {
        val json = JSONObject(application).apply { remove("actualState") }
        assertThrows(Exception::class.java) { ApplicationDeploymentWire.applications("[$json]") }
    }

    @Test fun `unfamiliar state is preserved without substituting stopped or running`() {
        val json = JSONObject(application).put("actualState", "futureState")
        assertEquals("futureState", ApplicationDeploymentWire.applications("[$json]").single().actualState)
    }

    @Test fun `snapshot reads active operation independently of recent history`() {
        val operation = """{"operationId":"op-1","applicationId":"d3708cc7-3e7e-42ad-b498-11466a48af23","kind":"deploy","state":"running","stage":"pulling",
            "progress":null,"problemCode":null,"recoveryProblemCode":null,"createdAt":"2026-09-26T12:00:00Z","cancellable":true}"""
        val snapshot = ApplicationDeploymentWire.snapshot("""{"application":$application,
            "revisions":[{"id":"rev-1","number":1,"imageReference":"image@sha256:abc","isCurrent":false}],
            "operations":[],"activeOperation":$operation}""")
        assertTrue(snapshot.operations.isEmpty())
        assertEquals("pulling", snapshot.activeOperation!!.stage)
        assertEquals("d3708cc7-3e7e-42ad-b498-11466a48af23", snapshot.activeOperation.applicationId)
        assertNull(snapshot.activeOperation.progress)
        assertTrue(snapshot.activeOperation.cancellable)
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

    @Test fun `templates preserve server defaults rather than inventing Android defaults`() {
        val template = ApplicationDeploymentWire.templates("""[{"sourceKind":"pythonProject","templateVersion":"1.0",
            "displayName":"Python project","defaultBaseImage":"python:3.13-slim","supportedPlatforms":["linux/amd64"],
            "requiresArchive":true,"requiresImageReference":false,"supportsSelfContained":false,"defaultContainerPort":8000}]""").single()
        assertEquals("pythonProject", template.sourceKind)
        assertEquals("python:3.13-slim", template.defaultBaseImage)
        assertTrue(template.requiresArchive)
        assertEquals(8000, template.defaultContainerPort)
    }

    @Test fun `catalogue keeps a server version and never gives a secret field a stored value`() {
        val template = ApplicationDeploymentWire.catalog("""[{"schemaVersion":"1","id":"file-service","version":"1.0.0",
            "publisher":"RelaxKonOS","source":"built-in","purpose":"File service","description":"Files",
            "supportedPlatforms":["linux/amd64"],"requiredCapabilities":["server.application-deployments"],
            "minimumResources":{"cpuCores":1,"memoryBytes":536870912,"pidsLimit":512},
            "fields":[{"id":"adminPassword","type":"secret","required":true,"defaultValue":null,"options":[],"labels":{"en":"Password","zh":"密码","ja":"パスワード"},"help":null}],
            "volumes":[{"name":"database","containerPath":"/database","readOnly":false}],"containerPort":80,"accessPath":"/","maintenanceNotes":"Keep data","withdrawn":false}]""").single()
        assertEquals("1", template.schemaVersion)
        assertEquals("1.0.0", template.version)
        assertEquals("secret", template.fields.single().type)
        assertNull(template.fields.single().defaultValue)
    }

    @Test fun `logs retain only bounded server output`() {
        val logs = ApplicationDeploymentWire.logs("""{"lines":["first","second"],"truncated":true}""")
        assertEquals(listOf("first", "second"), logs.lines)
        assertTrue(logs.truncated)
        assertThrows(Exception::class.java) { ApplicationDeploymentWire.logs("""{"lines":[]}""") }
    }
}
