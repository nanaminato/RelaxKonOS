package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.OperationalAlert
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AlertNotificationRulesTest {
    private val deployment = setOf(AlertNotificationCategory.Deployments)

    @Test
    fun `backup failures use the deployment notification policy`() {
        assertEquals(AlertNotificationCategory.Deployments,
            AlertNotificationCategories.forType("backup.definition_failed"))
    }

    @Test
    fun `first page is quiet and repeated occurrences do not notify twice`() {
        val first = AlertNotificationRules.decide(null, listOf(alert("a", count = 1)), deployment, true, 1_000_000L)
        assertTrue(first.notify.isEmpty())
        val repeated = AlertNotificationRules.decide(first.seen, listOf(alert("a", count = 2)), deployment, true, 1_001_000L)
        assertTrue(repeated.notify.isEmpty())
        val new = AlertNotificationRules.decide(repeated.seen,
            listOf(alert("a", count = 2), alert("b")), deployment, true, 1_002_000L)
        assertEquals(listOf(id("b")), new.notify.map { it.id })
        val again = AlertNotificationRules.decide(new.seen, listOf(alert("b", count = 5)), deployment,
            true, 2_000_000L)
        assertTrue(again.notify.isEmpty())
    }

    @Test
    fun `acknowledgement is quiet but a resolved alert can notify on a later reopening`() {
        val baseline = AlertNotificationRules.decide(null, emptyList(), deployment, true, 1_000_000L)
        val opened = AlertNotificationRules.decide(baseline.seen, listOf(alert("a")), deployment, true, 1_001_000L)
        assertEquals(1, opened.notify.size)
        val acknowledged = AlertNotificationRules.decide(opened.seen,
            listOf(alert("a", status = "acknowledged", count = 2, severity = "critical")),
            deployment, true, 1_002_000L)
        assertTrue(acknowledged.notify.isEmpty())
        assertEquals(listOf(id("a")), acknowledged.dismiss.map { it.id })
        val resolved = AlertNotificationRules.decide(acknowledged.seen,
            listOf(alert("a", status = "resolved", count = 2)), deployment, true, 1_003_000L)
        assertTrue(resolved.notify.isEmpty())
        val reopened = AlertNotificationRules.decide(resolved.seen,
            listOf(alert("a", status = "open", count = 3)), deployment, true, 2_000_000L)
        assertEquals(listOf(id("a")), reopened.notify.map { it.id })
    }

    @Test
    fun `severity upgrade respects cooldown and category policy`() {
        val baseline = AlertNotificationRules.decide(null, listOf(alert("a", severity = "warning")),
            deployment, true, 1_000_000L)
        val escalated = AlertNotificationRules.decide(baseline.seen,
            listOf(alert("a", severity = "critical")), deployment, true, 1_001_000L)
        assertEquals(listOf(id("a")), escalated.notify.map { it.id })
        val differentCategory = AlertNotificationRules.decide(escalated.seen,
            listOf(alert("certificate", type = "certificate.renewal_failed")), deployment, true, 1_002_000L)
        assertTrue(differentCategory.notify.isEmpty())
        val duplicate = AlertNotificationRules.decide(escalated.seen,
            listOf(alert("a", severity = "critical", count = 9)), deployment, true, 1_003_000L)
        assertTrue(duplicate.notify.isEmpty())
    }

    @Test
    fun `permission denial does not create a backlog and a page is rate limited`() {
        val baseline = AlertNotificationRules.decide(null, emptyList(), deployment, false, 1_000_000L)
        val denied = AlertNotificationRules.decide(baseline.seen, listOf(alert("a")), deployment,
            false, 1_001_000L)
        assertTrue(denied.notify.isEmpty())
        val granted = AlertNotificationRules.decide(denied.seen, listOf(alert("a")), deployment,
            true, 1_002_000L)
        assertTrue(granted.notify.isEmpty())
        val burst = AlertNotificationRules.decide(granted.seen,
            (1..10).map { alert("new-$it") }, deployment, true, 1_003_000L)
        assertEquals(3, burst.notify.size)
    }

    private fun alert(id: String, type: String = "deployment.operation_failed", status: String = "open",
        severity: String = "error", count: Int = 1) = OperationalAlert(id(id), type, severity, status,
        "deployment.failed", count, 1_000_000L, "applicationDeployment", null, null, 1_000_000L, "event", null, null, null)

    private fun id(value: String): String = UUID.nameUUIDFromBytes(value.toByteArray(Charsets.UTF_8)).toString()
}
