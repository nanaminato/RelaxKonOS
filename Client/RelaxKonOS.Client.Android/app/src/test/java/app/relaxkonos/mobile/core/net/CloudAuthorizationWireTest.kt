package app.relaxkonos.mobile.core.net

import org.junit.Assert.*
import org.junit.Test

class CloudAuthorizationWireTest {
    @Test fun `browser links accept only official https host`() {
        assertTrue(CloudAuthorizationWire.safeBaiduUrl("https://openapi.baidu.com/device"))
        for (url in listOf("http://openapi.baidu.com/device", "https://openapi.baidu.com.evil.test/device", "intent://openapi.baidu.com/device", "https://user@openapi.baidu.com/device", "https://openapi.baidu.com:8443/device"))
            assertFalse(CloudAuthorizationWire.safeBaiduUrl(url))
    }
    @Test fun `challenge is not included in debug representation`() {
        val session = CloudAuthorizationWire.session("""{"id":"18c1a12c-7434-4f50-bcda-615b8a658ac2","status":"pending","authorizationUrl":"https://openapi.baidu.com/device","userCode":"private-code","expiresAt":"2030-01-01T00:00:00Z","pollIntervalSeconds":5,"connectionId":null}""")
        assertTrue(session.pending)
        assertFalse(session.toString().contains("private-code"))
        assertFalse(session.toString().contains("https"))
        assertEquals(5, session.pollIntervalSeconds)
    }
    @Test fun `session routes reject traversal`() {
        assertTrue(runCatching { CloudAuthorizationWire.sessionRoute("../connections") }.isFailure)
        assertTrue(CloudAuthorizationWire.ROOT.startsWith("/api/v1.0/"))
    }
}
