package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * The 48dp header phones get by default: host, connection state, expand and session actions.
 *
 * Identity is the title because the destination is already named by the workspace navigation; the
 * connection line collapses into a state dot plus one short label instead of a full sentence, and
 * the retry entry appears in place of the expand control only while the shell is down.
 */
@Composable
internal fun TerminalTopBar(
    title: String,
    status: String,
    connected: Boolean,
    problem: Boolean,
    onExpand: () -> Unit,
    expandLabel: String,
    actions: @Composable () -> Unit,
    modifier: Modifier = Modifier,
    onBack: (() -> Unit)? = null,
    onRetry: (() -> Unit)? = null,
    retryLabel: String = "",
) {
    val stateColor = when {
        problem -> MaterialTheme.colorScheme.error
        connected -> MaterialTheme.colorScheme.primary
        else -> MaterialTheme.colorScheme.outline
    }
    Row(
        modifier.fillMaxWidth().heightIn(min = 48.dp).testTag("terminal-topbar"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // A top-level destination has no page-level back affordance; the shell owns that rule.
        val back = onBack
        if (back != null) IconButton(onClick = back) {
            DesktopIcon(DesktopIcons.back, size = 22.dp,
                contentDescription = stringResource(R.string.common_back))
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall,
                maxLines = 1, overflow = TextOverflow.Ellipsis)
            Row(verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Box(Modifier.size(7.dp).clip(CircleShape).background(stateColor))
                Text(status, style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        if (onRetry != null) IconButton(onClick = onRetry) {
            Icon(Icons.Default.Refresh, contentDescription = retryLabel, modifier = Modifier.size(22.dp))
        }
        IconButton(onClick = onExpand) {
            Icon(Icons.Default.KeyboardArrowDown, contentDescription = expandLabel,
                modifier = Modifier.size(24.dp))
        }
        actions()
    }
}
