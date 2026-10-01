package app.relaxkonos.mobile.ui.files

import org.junit.Assert.*
import org.junit.Test

class ViewerTransformTest {
    @Test fun `pan clamps to displayed image not the whole viewport`() {
        val t = ViewerTransform(2f, x = 10000f, y = -10000f).bounded(1000f, 500f, 1000, 1000)
        assertEquals(0f, t.x); assertEquals(-250f, t.y)
        val fit = t.fit().bounded(1000f, 500f, 1000, 1000)
        assertEquals(0f, fit.x); assertEquals(0f, fit.y)
    }
    @Test fun `quarter turns refit and repeated turns return to upright`() {
        var t = ViewerTransform(8f, x = 100f, y = 50f)
        repeat(4) { t = t.rotate(); assertEquals(1f, t.zoom); assertEquals(0f, t.x) }
        assertEquals(0, t.turns)
        assertEquals(250f, ViewerTransform(2f, 1, x = 10000f).bounded(500f, 1000f, 1000, 500).x)
    }
    @Test fun `invalid geometry and extreme gesture values cannot lose the image`() {
        assertEquals(ViewerTransform(turns = 1), ViewerTransform(8f, 1, 100f, 100f).bounded(0f, 0f, 100, 100))
        assertEquals(8f, ViewerTransform(100f).bounded(100f, 100f, 100, 100).zoom)
        assertEquals(.25f, ViewerTransform(-1f).bounded(100f, 100f, 100, 100).zoom)
        assertEquals(ViewerTransform(), ViewerTransform(Float.NaN, x = Float.NaN, y = Float.POSITIVE_INFINITY).bounded(100f, 100f, 100, 100))
    }
}
