package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class InstallationWireTest {
    // Verified against InstallationContracts + RelaxKonOSJsonOptions.Default, not enum ToString().
    private val operation = """{"operationId":"00112233-4455-6677-8899-aabbccddeeff","service":"nginx","kind":"install","state":"running","stage":"updatingPackageLists","progress":42,"problemCode":null,"createdAt":"2026-09-30T00:00:00+00:00","startedAt":null,"completedAt":null,"cancellable":true}"""

    @Test fun `current operation retains stage progress and nullable facts`() {
        val parsed = InstallationWire.operation(operation)
        assertEquals(InstallationService.Nginx, parsed.service)
        assertEquals(InstallationState.Running, parsed.state)
        assertEquals(InstallationStage.UpdatingPackageLists, parsed.stage)
        assertEquals(42, parsed.progress)
        assertNull(parsed.problemCode)
        assertNull(parsed.startedAtMillis)
        assertNull(parsed.completedAtMillis)
        assertTrue(parsed.cancellable)
    }

    @Test fun `unknown enum malformed timestamp and invalid progress are not healthy tasks`() {
        listOf(operation.replace("running", "futureState"), operation.replace("nginx", "Nginx"),
            operation.replace("42", "101"), operation.replace("2026-09-30T00:00:00+00:00", "invalid"))
            .forEach { assertTrue(runCatching { InstallationWire.operation(it) }.isFailure) }
    }

    @Test fun `six typed requests carry only their current protocol fields`() {
        val requests = listOf(SmbInstallationRequest(true), GitInstallationRequest(true), DockerInstallationRequest(true),
            NginxInstallationRequest(true, "1.28", "ref"), FrpInstallationRequest(true, "0.64", fileReferenceId = "ref"),
            MihomoInstallationRequest(true, "1.19", fileReferenceId = "ref"))
        requests.forEach { request ->
            val body = JSONObject(InstallationWire.request(request, InstallationKind.Install).toByteArray().decodeToString())
            assertTrue(body.getBoolean("confirmed"))
            assertFalse(body.has("service"))
            assertFalse(body.has("path"))
            if (!request.service.acceptsPackage) assertEquals(setOf("confirmed"), body.keys().asSequence().toSet())
            else assertEquals("ref", body.getString("fileReferenceId"))
        }
    }

    @Test fun `unsupported actions and package rollback combinations are rejected`() {
        assertTrue(runCatching { InstallationWire.request(GitInstallationRequest(true), InstallationKind.Upgrade) }.isFailure)
        assertTrue(runCatching { InstallationWire.request(NginxInstallationRequest(true, fileReferenceId = "ref"), InstallationKind.Repair) }.isFailure)
        assertTrue(runCatching { InstallationWire.request(FrpInstallationRequest(true, rollback = true), InstallationKind.Install) }.isFailure)
        assertTrue(runCatching { InstallationWire.request(MihomoInstallationRequest(true, rollback = true, fileReferenceId = "ref"), InstallationKind.Repair) }.isFailure)
        val rollback = JSONObject(InstallationWire.request(FrpInstallationRequest(true, rollback = true), InstallationKind.Repair).toByteArray().decodeToString())
        assertTrue(rollback.getBoolean("rollback"))
    }

    @Test fun `routes mirror shared route spelling and reject non GUID lookup ids`() {
        assertEquals("/api/v1.0/installations/Nginx/Install", InstallationRoutes.start(InstallationService.Nginx, InstallationKind.Install))
        assertEquals("/api/v1.0/installations/active?service=Mihomo", InstallationRoutes.active(InstallationService.Mihomo))
        assertEquals("/api/v1.0/installations/Frp/file-reference", InstallationRoutes.fileReference(InstallationService.Frp))
        assertEquals("/api/v1.0/installations/Frp/package", InstallationRoutes.packageUpload(InstallationService.Frp))
        assertTrue(runCatching { InstallationRoutes.operation("../active") }.isFailure)
    }

    @Test fun `file references expose expiry without retaining a server path`() {
        val reference = InstallationWire.fileReference("""{"id":"ref","fileName":"nginx.zip","length":12,"expiresAt":"2026-09-30T00:20:00Z"}""")
        assertFalse(reference.expired(reference.expiresAtMillis - 1))
        assertTrue(reference.expired(reference.expiresAtMillis))
    }
}
