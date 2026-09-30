package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

internal const val WEB_SITE = """{"id":"site1","serverId":"nginx","name":"site","bindings":[{"domain":"app.example.test","port":80}],"rootPath":"/srv/site","spaFallback":true,"routes":[{"path":"/api/","upstream":"http://127.0.0.1:8080","disableBuffering":true}],"certificateId":null,"httpsEnabled":false,"redirectHttpToHttps":false,"ipv6Enabled":true,"updatedAt":"2026-09-30T00:00:00.1234567Z","certificatePath":null,"privateKeyPath":null}"""
class WebServerSiteWireTest {
    @Test fun `full site definition retains flags paths and timestamp precision`() {
        val site = WebPublishingWire.site(WEB_SITE)
        assertEquals("2026-09-30T00:00:00.1234567Z", site.updatedAt)
        assertTrue(site.routes.single().disableBuffering)
        assertTrue(site.spaFallback)
        assertTrue(site.ipv6Enabled)
        assertEquals("/srv/site", site.rootPath)
        assertFalse(runCatching { WebPublishingWire.site(WEB_SITE.replace("\"ipv6Enabled\":true,", "")) }.isSuccess)
    }
    @Test fun `request sends observed timestamp and every editable field`() {
        val site = WebPublishingWire.site(WEB_SITE)
        val request = WebServerSiteRequest(site.id, site.name, site.bindings, site.rootPath, true, site.spaFallback,
            site.routes, null, false, false, site.ipv6Enabled, null, null, site.updatedAt)
        val json = JSONObject(request.body().toByteArray().decodeToString())
        assertEquals(site.updatedAt, json.getString("expectedUpdatedAt"))
        assertTrue(json.getBoolean("grantNginxReadAccess"))
        assertTrue(json.getJSONArray("routes").getJSONObject(0).getBoolean("disableBuffering"))
        assertTrue(request.matches(site))
    }
    @Test fun `site deletion encodes both dynamic segments`() {
        assertEquals("/api/v1.0/webservers/a%2Fb/sites/c%2Fd", WebPublishingRoutes.site("a/b", "c/d"))
    }
}
