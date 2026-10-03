package app.relaxkonos.mobile.servercenter

import org.junit.Assert.*
import org.junit.Test

class TransferProgressTest {
    @Test fun `SFTP callbacks accumulate bytes across packets and reset for each transfer`() {
        val values = mutableListOf<Double>()
        val monitor = FractionProgressMonitor(100, values::add)
        monitor.init(0, null, null, 100)
        monitor.count(20); monitor.count(30); monitor.count(50)
        assertEquals(listOf(0.0, 0.2, 0.5, 1.0), values)
        monitor.init(0, null, null, 100); monitor.count(10)
        assertEquals(0.1, values.last(), 0.0001)
    }

    @Test fun `remote transfer requires the current operation and accepts unknown totals`() {
        val id = "309c545c-d7bc-456b-b512-2edda5574585"
        fun record(bytes: Long, total: String, active: Boolean = true) =
            """{"operationId":"$id","bytes":$bytes,"total":$total,"active":$active}"""
        assertEquals(ServerDeploymentTransfer(50, 100), readDeploymentTransfer(record(50, "100"), id))
        assertEquals(ServerDeploymentTransfer(50, null), readDeploymentTransfer(record(50, "null"), id))
        assertNull(readDeploymentTransfer(record(100, "100", false), id))
        assertTrue(runCatching { readDeploymentTransfer(record(-1, "100"), id) }.isFailure)
        assertTrue(runCatching { readDeploymentTransfer(record(50, "100"), "00000000-0000-0000-0000-000000000000") }.isFailure)
    }
}
