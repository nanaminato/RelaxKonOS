package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.ui.theme.Spacing

@Composable internal fun TunnelCard(content: @Composable ColumnScope.() -> Unit) {
    OutlinedCard(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm), content = content)
    }
}

@Composable internal fun TunnelBadge(label: String, active: Boolean = false) {
    Surface(shape = MaterialTheme.shapes.small,
        color = if (active) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainerHigh,
        contentColor = if (active) MaterialTheme.colorScheme.onPrimaryContainer else MaterialTheme.colorScheme.onSurfaceVariant) {
        Text(label, Modifier.padding(horizontal = Spacing.sm, vertical = 4.dp), style = MaterialTheme.typography.labelLarge)
    }
}
