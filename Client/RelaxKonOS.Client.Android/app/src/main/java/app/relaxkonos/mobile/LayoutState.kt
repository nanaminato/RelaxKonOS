package app.relaxkonos.mobile

import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

enum class LayoutState { Compact, Medium, Expanded }

fun layoutStateFor(width: Dp): LayoutState = when {
    width < 600.dp -> LayoutState.Compact
    width < 840.dp -> LayoutState.Medium
    else -> LayoutState.Expanded
}
