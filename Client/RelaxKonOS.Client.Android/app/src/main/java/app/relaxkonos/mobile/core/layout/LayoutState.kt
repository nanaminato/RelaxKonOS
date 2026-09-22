package app.relaxkonos.mobile.core.layout

import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * The three window classes the shell adapts to.
 *
 * The breakpoints are the window size classes used by `RelaxKonOS.Mobile.V1.Design.md` §3: Compact
 * below 600dp, Medium from 600dp, Expanded from 840dp.
 */
enum class LayoutState { Compact, Medium, Expanded }

fun layoutStateFor(width: Dp): LayoutState = when {
    width < 600.dp -> LayoutState.Compact
    width < 840.dp -> LayoutState.Medium
    else -> LayoutState.Expanded
}
