package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Test

class EventAlertWireTest {
    private val alert = """{"alertId":"a","type":"deployment.failed","severity":"error","status":"open","firstOccurredAt":"2026-09-29T00:00:00Z","lastOccurredAt":"2026-09-29T00:01:00Z","occurrenceCount":2,"lastEventId":"e","problemCode":"deployment.failed","acknowledgedAt":null,"acknowledgedByReference":null,"resolutionReason":null,"remediationTarget":{"kind":"applicationDeploymentOperation","resourceId":"r","operationId":"o"}}"""

    @Test fun `reads the server alert projection and fixed remediation target`() {
        val page = EventAlertWire.page("""{"items":[$alert],"nextCursor":null}""")
        assertEquals(2, page.items.single().occurrenceCount)
        assertEquals("applicationDeploymentOperation", page.items.single().targetKind)
        assertNull(page.nextCursor)
        val detail = EventAlertWire.detail("""{"alert":$alert,"events":[{"outcome":"failed","occurredAt":"2026-09-29T00:01:00Z","problemCode":"deployment.failed"}],"actions":[]}""")
        assertEquals("failed", detail.events.single().outcome)
    }

    @Test fun `routes encode ids and cursors as single values`() {
        assertEquals("/api/v1.0/event-alerts/alerts/a%2Fb", EventAlertRoutes.alert("a/b"))
        assertEquals("/api/v1.0/event-alerts/alerts?pageSize=50&cursor=a%2Bb", EventAlertRoutes.page("a+b"))
    }

    @Test fun `missing required fields are rejected`() {
        assertFalse(runCatching { EventAlertWire.page("""{"items":[{"alertId":"a"}]}""") }.isSuccess)
    }
}
