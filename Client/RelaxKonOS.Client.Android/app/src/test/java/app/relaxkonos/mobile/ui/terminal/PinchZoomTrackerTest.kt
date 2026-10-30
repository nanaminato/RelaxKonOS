package app.relaxkonos.mobile.ui.terminal

import org.junit.Assert.assertEquals
import org.junit.Test

class PinchZoomTrackerTest {
    @Test fun oneStepPerThresholdAndTheBaselineMovesWithIt() {
        val tracker = PinchZoomTracker(threshold = 0.15f)
        tracker.start(100f)
        assertEquals(0, tracker.update(110f))
        assertEquals(0, tracker.update(114f))
        assertEquals(1, tracker.update(116f))
        assertEquals(0, tracker.update(120f))
        assertEquals(-1, tracker.update(98f))
    }

    @Test fun resetAndMissingSpanNeverEmit() {
        val tracker = PinchZoomTracker()
        assertEquals(0, tracker.update(200f))
        tracker.start(0f)
        assertEquals(0, tracker.update(200f))
        tracker.start(100f)
        tracker.reset()
        assertEquals(0, tracker.update(200f))
    }

    @Test fun zeroLengthSpanIsIgnored() {
        val tracker = PinchZoomTracker(threshold = 0.1f)
        tracker.start(100f)
        assertEquals(0, tracker.update(0f))
        assertEquals(1, tracker.update(112f))
    }
}
