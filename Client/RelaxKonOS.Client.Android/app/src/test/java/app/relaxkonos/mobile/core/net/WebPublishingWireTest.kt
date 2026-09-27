package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class WebPublishingWireTest {
    @Test fun `routes retain the protocol base and encode a server id as one segment`() {
        assertEquals("/api/v1.0/webservers", WebPublishingRoutes.servers())
        assertEquals("/api/v1.0/certificates", WebPublishingRoutes.CERTIFICATES)
        assertEquals("/api/v1.0/webservers/managed%2Fnginx/status", WebPublishingRoutes.status("managed/nginx"))
        assertEquals("/api/v1.0/webservers/managed%2Fnginx/config/test", WebPublishingRoutes.testConfiguration("managed/nginx"))
    }

    @Test fun `reads server site and certificate diagnostics without certificate material`() {
        val server = WebPublishingWire.servers("""[{"id":"nginx1","type":"nginx","managementMode":"managed","version":null,"capabilities":{"canRead":true,"canTestConfiguration":true}}]""").single()
        assertEquals("nginx1", server.id)
        assertNull(server.version)
        assertTrue(server.canTestConfiguration)

        val status = WebPublishingWire.status("""{"instanceId":"nginx1","runtimeState":"running","problemCode":""}""")
        assertEquals("running", status.runtimeState)
        assertEquals(WebServerConfigTest(false, "webserver.config_test_failed"),
            WebPublishingWire.configTest("""{"valid":false,"problemCode":"webserver.config_test_failed"}"""))

        val site = WebPublishingWire.sites("""[{"id":"site1","serverId":"nginx1","name":"app","bindings":[{"domain":"app.example.test","port":443}],"rootPath":null,"spaFallback":false,"routes":[{"path":"/","upstream":"127.0.0.1:8080","disableBuffering":false}],"certificateId":"0fded9ef-ed50-4d5e-8e87-74caf3311be7","httpsEnabled":true,"redirectHttpToHttps":true,"ipv6Enabled":false,"updatedAt":"2026-09-27T00:00:00Z"}]""").single()
        assertEquals("app.example.test", site.bindings.single().domain)
        assertEquals("127.0.0.1:8080", site.routes.single().upstream)
        assertEquals("0fded9ef-ed50-4d5e-8e87-74caf3311be7", site.certificateId)

        val certificate = WebPublishingWire.certificates("""[{"id":"0fded9ef-ed50-4d5e-8e87-74caf3311be7","primaryDomain":"app.example.test","subjectAlternativeNames":[],"issuer":null,"serialNumber":null,"thumbprint":null,"notBefore":null,"notAfter":null,"status":"issued","challengeType":"directHttp01","keyAlgorithm":"ecdsaP256","renewalWindowStart":null,"renewalWindowEnd":null,"lastRenewalAt":null,"lastRenewalProblemCode":null,"createdAt":"2026-09-27T00:00:00Z","updatedAt":"2026-09-27T00:00:00Z"}]""").single()
        assertEquals("app.example.test", certificate.primaryDomain)
        assertEquals("issued", certificate.status)
        assertNull(certificate.notAfterMillis)
    }

    @Test fun `malformed required web publishing fields fail closed`() {
        assertFalse(runCatching { WebPublishingWire.status("""{"runtimeState":"running","problemCode":""}""") }.isSuccess)
        assertFalse(runCatching { WebPublishingWire.sites("""[{"id":"site","serverId":"server"}]""") }.isSuccess)
    }
}
