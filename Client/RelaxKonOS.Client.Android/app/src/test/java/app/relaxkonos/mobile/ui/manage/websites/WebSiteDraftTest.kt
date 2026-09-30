package app.relaxkonos.mobile.ui.manage.websites

import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class WebSiteDraftTest {
    private val site = WebPublishingWire.site(WEB_SITE)
    @Test fun `editing a full definition retains every field and its exact concurrency token`() {
        val request = WebSiteDraft.from(site).request()!!
        assertTrue(request.matches(site))
        assertEquals(site.updatedAt, request.expectedUpdatedAt)
    }
    @Test fun `new site uses stable explicit ID across draft copies`() {
        val draft = WebSiteDraft("nginx").copy(name = "new", rootPath = "/srv/new", bindings = listOf(SiteBindingDraft("app.example.test")))
        assertEquals(draft.id, draft.copy(name = "renamed").request()!!.id)
        assertNull(draft.request()!!.expectedUpdatedAt)
    }
    @Test fun `invalid port content and duplicate route prefixes block submission`() {
        val draft = WebSiteDraft.from(site)
        assertNull(draft.copy(bindings = listOf(SiteBindingDraft("app.example.test", "0"))).request())
        assertNull(draft.copy(rootPath = "", routes = emptyList()).request())
        assertNull(draft.copy(routes = listOf(SiteRouteDraft("/api/", "http://localhost:8080"), SiteRouteDraft("/api/", "http://localhost:9090"))).request())
    }
    @Test fun `upstream credentials and shell-like route prefixes are refused`() {
        val draft = WebSiteDraft.from(site)
        assertNull(draft.copy(routes = listOf(SiteRouteDraft("/", "http://user:secret@example.test"))).request())
        assertNull(draft.copy(routes = listOf(SiteRouteDraft("/;", "http://localhost:8080"))).request())
    }
    @Test fun `HTTPS requires a managed certificate or a host file and preserves combined PEM mode`() {
        val draft = WebSiteDraft.from(site)
        assertNull(draft.copy(httpsEnabled = true, certificateId = null).request())
        val request = draft.copy(httpsEnabled = true, useServerCertificate = true, certificatePath = "/srv/tls.pem").request()!!
        assertEquals("/srv/tls.pem", request.certificatePath)
        assertNull(request.privateKeyPath)
        assertNull(request.certificateId)
    }
    @Test fun `certificate ID and redirect controls follow the protocol`() {
        val draft = WebSiteDraft.from(site)
        assertNull(draft.copy(httpsEnabled = false, redirectHttpToHttps = true).request())
        assertNull(draft.copy(certificateId = "bad").request())
        assertNotNull(draft.copy(httpsEnabled = true, certificateId = "00112233-4455-6677-8899-aabbccddeeff").request())
    }
}
