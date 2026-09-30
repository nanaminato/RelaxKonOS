package app.relaxkonos.mobile.core.net

import org.junit.Assert.*
import org.junit.Test

internal const val WEB_OPERATION = """{"operationId":"00112233-4455-6677-8899-aabbccddeeff","instanceId":"nginx","kind":"reload","state":"running","stage":"running","problemCode":"","snapshotId":null,"startedAt":null,"completedAt":null}"""
internal const val WEB_SERVER = """[{"id":"nginx","providerId":"nginx","type":"nginx","managementMode":"managed","executablePath":"/usr/sbin/nginx","configurationPath":"/etc/nginx/nginx.conf","version":"1.31.3","detectedAt":"2026-09-30T00:00:00Z","capabilities":{"canRead":true,"canTestConfiguration":true,"canReload":true,"canStart":true,"canStop":true,"canRestart":true,"canUninstall":true}}]"""
class WebServerManagementWireTest {
    @Test fun `dynamic lifecycle and candidate routes preserve single segments`() {
        assertEquals("/api/v1.0/webservers/a%2Fb/lifecycle/reload", WebPublishingRoutes.lifecycle("a/b", WebServerAction.Reload))
        assertEquals("/api/v1.0/webservers/integration-candidates/a%2Fb/integrate", WebPublishingRoutes.integrate("a/b"))
        assertEquals("/api/v1.0/webservers/nginx/lifecycle/enableacmehttp01", WebPublishingRoutes.lifecycle("nginx", WebServerAction.EnableAcmeHttp01))
        assertEquals("enable-acme-http01", WebServerAction.EnableAcmeHttp01.kind)
    }
    @Test fun `operation IDs are strict and normalized`() {
        val op = WebPublishingWire.operation(WEB_OPERATION)
        assertTrue(op.state.active)
        assertEquals("nginx", op.instanceId)
        assertFalse(runCatching { WebPublishingRoutes.operation("../bad") }.isSuccess)
        assertFalse(runCatching { WebPublishingWire.operation(WEB_OPERATION.replace("running", "interrupted")) }.isSuccess)
    }
    @Test fun `capabilities are authoritative for each instance`() {
        val server = WebPublishingWire.servers(WEB_SERVER).single()
        assertTrue(WebServerAction.Start.supported(server))
        assertFalse(WebServerAction.Start.supported(server.copy(canStart = false)))
        assertTrue(WebServerAction.Reload.supported(server.copy(managementMode = "integrated")))
        assertFalse(runCatching { WebPublishingWire.servers(WEB_SERVER.replace("\"canReload\":true,", "")) }.isSuccess)
    }
    @Test fun `catalog and candidates read current contract`() {
        val catalog = WebPublishingWire.catalog("""{"mainlineVersion":"1.31.3","stableVersion":null,"versions":["1.31.3"],"problemCode":""}""")
        assertEquals(listOf("1.31.3"), catalog.versions)
        assertNull(catalog.stableVersion)
        val candidate = WebPublishingWire.candidates("""[{"id":"external","providerId":"nginx","type":"nginx","executablePath":"/usr/sbin/nginx","configurationPath":null,"version":null,"detectedAt":"2026-09-30T00:00:00Z"}]""").single()
        assertNull(candidate.configurationPath)
        assertFalse(runCatching { WebPublishingWire.candidates("""[{"id":"external"}]""") }.isSuccess)
    }
}
