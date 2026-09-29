package app.relaxkonos.mobile.ui.manage.operations

import app.relaxkonos.mobile.data.ObservedOperation
import app.relaxkonos.mobile.data.OperationCheck
import app.relaxkonos.mobile.data.OperationDomain
import app.relaxkonos.mobile.data.OperationReference
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class OperationDiagnosticReportTest {
    @Test
    fun `report identifies its owner and bounds diagnostic output`() {
        val item = ObservedOperation(
            OperationReference("server-1", "alice", OperationDomain.Script, "task-1", "task-1", 1000L),
            OperationCheck.Verified, "run.sh", "failed", checkedAtMillis = 2000L,
        )
        val report = JSONObject(OperationDiagnosticReport.create(item, List(101) { "x".repeat(600) }, true))
        assertEquals("server-1", report.getString("serviceId"))
        assertEquals("task-1", report.getString("operationId"))
        assertEquals("1970-01-01T00:00:01.000Z", report.getString("observedFromUtc"))
        assertEquals(100, report.getJSONArray("diagnostics").length())
        assertEquals(512, report.getJSONArray("diagnostics").getString(0).length)
        assertTrue(report.getBoolean("diagnosticsTruncated"))
        assertTrue(report.getBoolean("diagnosticsAvailable"))
        assertFalse(report.has("account"))
    }
}
