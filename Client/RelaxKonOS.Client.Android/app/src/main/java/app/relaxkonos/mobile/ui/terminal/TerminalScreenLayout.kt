package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
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
    header: @Composable (compact: Boolean) -> Unit,
    output: @Composable () -> Unit,
    keys: @Composable () -> Unit,
    input: @Composable (compact: Boolean) -> Unit,
) {
    BoxWithConstraints(modifier.fillMaxSize().windowInsetsPadding(imeInsets)) {
        val fontScale = LocalDensity.current.fontScale
        val compact = maxHeight < 400.dp * fontScale
        val showKeys = maxHeight >= 200.dp * fontScale
        Column(
            Modifier.fillMaxSize().padding(horizontal = Spacing.lg, vertical = Spacing.sm),
            verticalArrangement = Arrangement.spacedBy(Spacing.xs),
        ) {
            // Non-weighted children are measured first by Column. Only this editor is outside the
            // weighted body; the remaining controls adapt to whatever height it leaves behind.
            Column(Modifier.fillMaxWidth().weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                header(compact)
                Box(Modifier.fillMaxWidth().weight(1f)) { output() }
                if (showKeys) keys()
            }
            input(compact)
        }
    }
}
