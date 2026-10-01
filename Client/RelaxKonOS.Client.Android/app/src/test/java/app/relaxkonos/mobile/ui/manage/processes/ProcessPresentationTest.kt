package app.relaxkonos.mobile.ui.manage.processes

import app.relaxkonos.mobile.core.net.RemoteProcess
import org.junit.Assert.*
import org.junit.Test

class ProcessPresentationTest {
    private val original = RemoteProcess(42, "old", 0.0, 10, null, 1, "2026-10-01T00:00:00Z")
    @Test fun `refresh updates current metrics only for the same instance`() {
        val next = original.copy(cpuPercent = 20.0, memoryBytes = 20, threadCount = 2)
        assertEquals(next, refreshedProcessSelection(original, listOf(next)))
        assertNull(refreshedProcessSelection(original, listOf(next.copy(startTime = "2026-10-01T00:00:01Z"))))
        assertNull(refreshedProcessSelection(original, emptyList()))
    }
    @Test fun `unverifiable instance cannot retain a selection across a new snapshot`() {
        assertNull(refreshedProcessSelection(original.copy(startTime = null), listOf(original.copy(startTime = null))))
        assertNull(refreshedProcessSelection(null, listOf(original)))
    }
}
