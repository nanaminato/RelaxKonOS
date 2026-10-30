package app.relaxkonos.mobile.ui.terminal

/**
 * Turns a continuous pinch span into discrete font-size steps.
 *
 * The terminal owns its own one-finger scrolling and text selection, so a pinch must not be
 * converted into a stream of resize requests: the span has to change by [threshold] before one step
 * is emitted, and the baseline then moves with it. The tracker keeps no Compose state so it can be
 * used from both the WebView touch handler and a Compose pointer loop.
 */
internal class PinchZoomTracker(private val threshold: Float = 0.15f) {
    private var baseline = 0f

    /** Record the span of the first two-finger frame; also used when a finger is added or removed. */
    fun start(span: Float) {
        baseline = if (span > 0f) span else 0f
    }

    /**
     * @return `1` for a smaller font, `-1` for a larger one, `0` while the pinch has not moved far
     * enough. The caller applies the step, so both terminals can clamp to their own font range.
     */
    fun update(span: Float): Int {
        if (baseline <= 0f || span <= 0f) return 0
        val ratio = span / baseline
        return when {
            ratio >= 1f + threshold -> {
                baseline = span
                1
            }
            ratio <= 1f - threshold -> {
                baseline = span
                -1
            }
            else -> 0
        }
    }

    fun reset() {
        baseline = 0f
    }
}
