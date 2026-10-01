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
        val detail = EventAlertWire.detail("""{"alert":$alert,"events":[{"eventId":"e","outcome":"failed","occurredAt":"2026-09-29T00:01:00Z","type":"deployment.operation_failed","severity":"error","source":"deployment","problemCode":"deployment.failed","correlationId":"c","operationId":null,"resourceType":"application","resourceReference":"r","evidence":null,"remediationTarget":{"kind":"none","resourceId":null,"operationId":null}}],"actions":[]}""")
        assertEquals("failed", detail.events.single().outcome)
    }

    @Test fun `routes encode ids and cursors as single values`() {
        assertEquals("/api/v1.0/event-alerts/alerts/a%2Fb", EventAlertRoutes.alert("a/b"))
        assertEquals("/api/v1.0/event-alerts/alerts?pageSize=50&cursor=a%2Bb", EventAlertRoutes.page("a+b"))
    }

    @Test fun `missing required fields are rejected`() {
        assertFalse(runCatching { EventAlertWire.page("""{"items":[{"alertId":"a"}]}""") }.isSuccess)
    }

    @Test fun `filters encode query values and preserve server vocabulary`() {
        assertEquals("/api/v1.0/event-alerts/events?pageSize=50&cursor=a%2Bb&severity=critical&source=eventCenter&type=a%2Fb",
            EventAlertRoutes.events("a+b", EventQuery(EventSeverityFilter.Critical, EventSourceFilter.EventCenter, "a/b")))
        assertEquals("/api/v1.0/event-alerts/alerts?pageSize=50&status=suppressed&severity=warning",
            EventAlertRoutes.page(null, AlertQuery(AlertStatusFilter.Suppressed, EventSeverityFilter.Warning)))
    }
    @Test fun `summary retains server counts and explicit nullable fields`() {
        val summary = EventAlertWire.summary("""{"openCount":2,"acknowledgedCount":1,"unacknowledgedCriticalCount":1,"highestUnacknowledgedSeverity":"critical","updatedAt":null}""")
        assertEquals(2, summary.openCount); assertNull(summary.updatedAtMillis)
        assertFalse(runCatching { EventAlertWire.summary("""{"openCount":0,"acknowledgedCount":0,"unacknowledgedCriticalCount":0}""") }.isSuccess)
    }
    @Test fun `complete action history is required and preserves safe references`() {
        val detail = EventAlertWire.detail("""{"alert":$alert,"events":[],"actions":[{"actionId":"action","kind":"acknowledged","actorReference":"hmac:v1:safe","note":"checked","createdAt":"2026-09-29T00:02:00Z"}]}""")
        assertEquals("hmac:v1:safe", detail.actions.single().actorReference)
        assertFalse(runCatching { EventAlertWire.detail("""{"alert":$alert,"events":[]}""") }.isSuccess)
    }
}
