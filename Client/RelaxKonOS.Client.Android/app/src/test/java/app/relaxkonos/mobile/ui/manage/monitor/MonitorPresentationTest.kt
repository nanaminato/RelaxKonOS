package app.relaxkonos.mobile.ui.manage.monitor

import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class MonitorPresentationTest {
    @Test fun `resource identity includes its domain and changes only with the real target`() {
        val info = PerformanceWire.info(PerformanceWireTest.INFO)
        val snapshot = PerformanceWire.snapshot(PerformanceWireTest.SNAPSHOT)
        val resources = performanceResources(info, snapshot)
        assertEquals(5, resources.size)
        assertEquals(resources.size, resources.map { it.key }.distinct().size)
        assertEquals("data", resources.first { it.kind == PerformanceKind.Filesystem }.name)
        val hotplug = snapshot.copy(networks = snapshot.networks + snapshot.networks.single().copy(id = "extra"))
        val next = performanceResources(info, hotplug)
        assertTrue(next.containsAll(resources)); assertEquals(6, next.size)
        assertNull(next.single { it.id == "extra" }.name)
        assertTrue(performanceResources(null, null).isEmpty())
    }
    @Test fun `two pane layout considers remaining height and font scale`() {
        assertFalse(monitorUsesTwoPanes(839f, 800f, 1f))
        assertTrue(monitorUsesTwoPanes(840f, 240f, 1f))
        assertFalse(monitorUsesTwoPanes(1000f, 239f, 1f))
        assertFalse(monitorUsesTwoPanes(840f, 500f, 1.5f))
        assertFalse(monitorUsesTwoPanes(1500f, 300f, 1.5f))
        assertTrue(monitorUsesTwoPanes(1500f, 500f, 1.5f))
    }
    @Test fun `trend paths split around actual missing stale and out of order samples`() {
        val values = listOf(TrendPoint(0, 0.0), TrendPoint(1000, 10.0), TrendPoint(2000, null), TrendPoint(3000, 30.0),
            TrendPoint(8000, 50.0), TrendPoint(7000, 60.0), TrendPoint(9000, Double.NaN))
        assertEquals(listOf(listOf(0L, 1000L), listOf(3000L), listOf(8000L), listOf(7000L)), trendSegments(values).map { it.map { point -> point.timeMillis } })
        val stale = PerformanceWire.snapshot(PerformanceWireTest.SNAPSHOT)
        assertNull(trendPoints(listOf(stale)) { it.cpu.totalPercent }.single().value)
        assertEquals(0.0, trendSegments(listOf(TrendPoint(0, 0.0))).single().single().value!!, 0.0)
    }
}
