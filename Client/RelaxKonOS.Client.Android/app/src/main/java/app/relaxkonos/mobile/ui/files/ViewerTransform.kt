package app.relaxkonos.mobile.ui.files

import kotlin.math.max
import kotlin.math.min

data class ViewerTransform(val zoom: Float = 1f, val turns: Int = 0, val x: Float = 0f, val y: Float = 0f) {
    fun fit() = ViewerTransform(turns = turns)
    fun rotate() = ViewerTransform(turns = (turns + 1) % 4)
    fun bounded(viewWidth: Float, viewHeight: Float, imageWidth: Int, imageHeight: Int): ViewerTransform {
        if (viewWidth <= 0 || viewHeight <= 0 || imageWidth <= 0 || imageHeight <= 0) return fit()
        val width = if (turns % 2 == 0) imageWidth.toFloat() else imageHeight.toFloat()
        val height = if (turns % 2 == 0) imageHeight.toFloat() else imageWidth.toFloat()
        val fit = min(viewWidth / width, viewHeight / height)
        val scale = zoom.takeIf { it.isFinite() }?.coerceIn(.25f, 8f) ?: 1f
        val maxX = max(0f, (width * fit * scale - viewWidth) / 2)
        val maxY = max(0f, (height * fit * scale - viewHeight) / 2)
        return copy(zoom = scale, x = x.takeIf { it.isFinite() }?.coerceIn(-maxX, maxX) ?: 0f,
            y = y.takeIf { it.isFinite() }?.coerceIn(-maxY, maxY) ?: 0f)
    }
}
