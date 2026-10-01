package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.heightIn
import androidx.compose.ui.platform.testTag
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Measure the command editor before the weighted body so chrome cannot compress its text/cursor.
 * IME padding respects insets consumed by the shell (including the tablet's safe drawing padding).
 */
@Composable
internal fun TerminalScreenLayout(
    modifier: Modifier = Modifier,
    imeInsets: WindowInsets = WindowInsets.ime,
    header: @Composable (compact: Boolean, wide: Boolean) -> Unit,
    sidebar: @Composable () -> Unit,
    output: @Composable () -> Unit,
    keys: @Composable () -> Unit,
    input: @Composable (compact: Boolean) -> Unit,
) {
    BoxWithConstraints(modifier.fillMaxSize().windowInsetsPadding(imeInsets)) {
        val density = LocalDensity.current
        val fontScale = density.fontScale
        // A tall phone can retain >400dp above its IME. Still fold management controls while
        // typing so the transcript gets that space, including when a parent consumed the inset.
        val compact = imeInsets.getBottom(density) > 0 || maxHeight < 400.dp * fontScale
        val showKeys = maxHeight >= 200.dp * fontScale
        val wide = terminalTwoPane(maxWidth.value, maxHeight.value, fontScale)
        Row(Modifier.fillMaxSize().padding(horizontal = Spacing.lg, vertical = Spacing.sm),
            horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
            if (wide) Box(Modifier.width(240.dp).fillMaxSize().testTag("terminal-session-sidebar")) { sidebar() }
            Column(Modifier.weight(1f).fillMaxSize(), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                // The editor is measured before the weighted body on phone and tablet alike.
                Column(Modifier.fillMaxWidth().weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                    header(compact, wide)
                    Box(Modifier.fillMaxWidth().weight(1f)) { output() }
                    if (showKeys) keys()
                }
                input(compact)
            }
        }
    }
}

internal fun terminalTwoPane(widthDp: Float, heightDp: Float, fontScale: Float): Boolean =
    widthDp >= 840f && heightDp >= 200f * fontScale
