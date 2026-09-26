package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithCache
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import app.relaxkonos.mobile.ui.theme.relaxKon

/**
 * The page backdrop.
 *
 * A flat fill reads as a wireframe; this paints a soft vertical gradient with two off-screen glows so
 * surfaces above it have something to sit on. Everything it draws is a theme token, so light, dark and
 * high contrast each get their own version without a single value hard-coded here.
 *
 * The glows are deliberately faint — they are depth, not decoration, and text contrast must survive
 * them. High contrast collapses the gradient to near-flat because legibility outranks depth there.
 *
 * Use it once per window (the shell and the sign-in screen), not per screen: nesting it would multiply
 * the gradients.
 */
@Composable
fun AppBackdrop(
    modifier: Modifier = Modifier,
    content: @Composable BoxScope.() -> Unit,
) {
    val colors = MaterialTheme.relaxKon
    Box(
        modifier = modifier
            .fillMaxSize()
            .drawWithCache {
                val vertical = Brush.verticalGradient(listOf(colors.backdropStart, colors.backdropEnd))
                val upperGlow = Brush.radialGradient(
                    colors = listOf(colors.glowPrimary, Color.Transparent),
                    center = Offset(size.width * 0.15f, size.height * -0.05f),
                    radius = size.maxDimension * 0.8f,
                )
                val lowerGlow = Brush.radialGradient(
                    colors = listOf(colors.glowSecondary, Color.Transparent),
                    center = Offset(size.width * 1.1f, size.height * 0.9f),
                    radius = size.maxDimension * 0.65f,
                )
                onDrawBehind {
                    drawRect(brush = vertical)
                    drawRect(brush = upperGlow)
                    drawRect(brush = lowerGlow)
                }
            },
        content = content,
    )
}
