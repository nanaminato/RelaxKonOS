package app.relaxkonos.mobile.ui.connect

import org.junit.Assert.*
import org.junit.Test

class PairingScanStateTest {
    @Test fun `scan result cannot replace edited code or submit during enrollment`() {
        val scans = PairingScanState(); scans.attach()
        val edited = requireNotNull(scans.begin("first", false))
        assertNull(scans.begin("first", false))
        assertFalse(scans.finish(edited, "second", false)); assertFalse(scans.busy)
        val busy = requireNotNull(scans.begin("second", false))
        assertFalse(scans.finish(busy, "second", true)); assertFalse(scans.busy)
        assertNull(scans.begin("second", true))
    }
    @Test fun `late success or cancellation cannot consume a newer scan`() {
        val scans = PairingScanState(); scans.attach()
        val old = requireNotNull(scans.begin("code", false)); scans.cancel(old)
        val fresh = requireNotNull(scans.begin("code", false))
        scans.cancel(old); assertTrue(scans.busy)
        assertFalse(scans.finish(old, "code", false)); assertTrue(scans.busy)
        assertTrue(scans.finish(fresh, "code", false))
        assertFalse(scans.finish(fresh, "code", false)); assertFalse(scans.busy)
    }
    @Test fun `leaving screen invalidates scan even if new screen has same code`() {
        val scans = PairingScanState(); scans.attach()
        val old = requireNotNull(scans.begin("code", false)); scans.detach()
        assertFalse(scans.busy); assertFalse(scans.finish(old, "code", false))
        scans.attach(); val fresh = requireNotNull(scans.begin("code", false))
        assertFalse(scans.finish(old, "code", false)); assertTrue(scans.finish(fresh, "code", false))
    }
}
