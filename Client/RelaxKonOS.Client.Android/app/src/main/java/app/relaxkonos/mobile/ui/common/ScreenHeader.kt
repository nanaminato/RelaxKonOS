package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.core.layout.layoutStateFor
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * The header every destination starts with.
 *
 * One composable rather than a `Text(headlineSmall)` per screen, so the title size, the gap to the
 * content and the back affordance cannot drift apart. [onBack] is `null` when the screen is rendered
 * as a pane in the Expanded layout, and a pane has nothing to go back from — a visible but inert
 * button would misdescribe the layout (`Shell.Design.md` §4.1).
 */
@Composable
fun ScreenHeader(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    onBack: (() -> Unit)? = null,
    backAlignment: Alignment.Vertical = Alignment.CenterVertically,
    trailing: (@Composable () -> Unit)? = null,
) {
    val workspace = LocalWorkspaceHeader.current
    if (workspace != null && workspace.screenTitle == title) {
        SideEffect {
            workspace.back = onBack
            workspace.hasBack = onBack != null
        }
        if (trailing != null) Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.CenterEnd) { trailing() }
        return
    }
    BoxWithConstraints(modifier = modifier.fillMaxWidth()) {
        val titleContent: @Composable (Modifier) -> Unit = { titleModifier ->
            Column(titleModifier) {
                Text(title, style = MaterialTheme.typography.headlineSmall)
                if (subtitle != null) {
                    Text(
                        subtitle,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
        val backButton: @Composable RowScope.() -> Unit = {
            if (onBack != null) {
                FilledTonalIconButton(onClick = onBack, modifier = Modifier.align(backAlignment)) {
                    DesktopIcon(
                        icon = DesktopIcons.back,
                        size = 22.dp,
                        contentDescription = stringResource(R.string.common_back),
                    )
                }
            }
        }

        if (layoutStateFor(maxWidth) == LayoutState.Compact && trailing != null) {
            // A phone cannot reliably fit a back button, a localized title, and several text actions
            // on one row. Keep the title at its readable width and put the action group below it.
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(Spacing.md),
                ) {
                    backButton()
                    titleContent(Modifier.weight(1f))
                }
                Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.CenterEnd) { trailing() }
            }
        } else {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(Spacing.md),
            ) {
                backButton()
                titleContent(Modifier.weight(1f))
                trailing?.invoke()
            }
        }
    }
}
