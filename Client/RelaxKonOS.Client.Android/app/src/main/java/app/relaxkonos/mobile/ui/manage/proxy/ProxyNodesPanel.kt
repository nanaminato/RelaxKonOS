package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.selectable
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*

@Composable
internal fun ProxyNodesPanel(state: ProxyState, canManage: Boolean, ready: Boolean, onRefresh: () -> Unit,
                            onSelect: (ProxyGroup, String) -> Unit, onTestGroup: (ProxyGroup) -> Unit) {
    val groups = (state.groups as? ApiResult.Success)?.value
    var selectedName by rememberSaveable { mutableStateOf<String?>(null) }
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(stringResource(R.string.mihomo_nodes_hint), Modifier.weight(1f), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        IconButton(enabled = !state.busy, onClick = onRefresh) {
            Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.common_refresh))
        }
    }
    state.problemCode?.let { Text(proxyProblemLabel(it), color = MaterialTheme.colorScheme.error) }
    if (state.uncertain) Text(stringResource(R.string.mihomo_uncertain), color = MaterialTheme.colorScheme.error)
    if (groups == null) { Text(stringResource(R.string.mihomo_unavailable)); return }
    if (groups.isEmpty()) { Text(stringResource(R.string.mihomo_nodes_empty)); return }
    val index = groups.indexOfFirst { it.name == selectedName }.takeIf { it >= 0 } ?: 0
    val group = groups[index]
    SecondaryScrollableTabRow(selectedTabIndex = index, edgePadding = 0.dp, modifier = Modifier.fillMaxWidth()) {
        groups.forEach { item ->
            Tab(selected = group.name == item.name, onClick = { selectedName = item.name },
                text = { Text(item.name, maxLines = 1, overflow = TextOverflow.Ellipsis) })
        }
    }
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(stringResource(R.string.mihomo_group_summary, group.type, group.proxies.size), style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(stringResource(R.string.mihomo_selected_node, group.selected ?: "—"), style = MaterialTheme.typography.bodySmall)
            if (!group.selectable) Text(stringResource(R.string.mihomo_group_automatic), style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (canManage) IconButton(enabled = ready && group.proxies.isNotEmpty(), onClick = { onTestGroup(group) }) {
            Icon(painterResource(R.drawable.ic_proxy_bolt), contentDescription = stringResource(R.string.mihomo_test_group_delay, group.name),
                tint = MaterialTheme.colorScheme.primary)
        }
    }
    if (state.testingGroup != null) {
        val testing = groups.firstOrNull { it.name == state.testingGroup }
        val total = testing?.proxies?.distinct()?.size ?: 0
        Text(stringResource(R.string.mihomo_group_testing, state.testingGroup, total - state.testingProxies.size, total),
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
        LinearProgressIndicator(Modifier.fillMaxWidth())
    } else if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val columns = (maxWidth / 140.dp).toInt().coerceIn(1, 4)
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            group.proxies.chunked(columns).forEach { nodes ->
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    nodes.forEach { proxy -> key(group.name, proxy) {
                        ProxyNodeCard(proxy, group.selected == proxy, canManage && ready && group.selectable && group.selected != proxy,
                            state.delays[proxy], proxy in state.testingProxies, Modifier.weight(1f)) { onSelect(group, proxy) }
                    } }
                    repeat(columns - nodes.size) { Spacer(Modifier.weight(1f)) }
                }
            }
        }
    }
}

@Composable
private fun ProxyNodeCard(name: String, selected: Boolean, enabled: Boolean, result: ApiResult<ProxyDelay>?, testing: Boolean,
                          modifier: Modifier, onSelect: () -> Unit) {
    val delay = (result as? ApiResult.Success)?.value
    val failed = result != null && (result !is ApiResult.Success || delay?.timedOut == true || delay?.delayMilliseconds == null)
    val latencyText = when {
        testing -> stringResource(R.string.mihomo_node_testing)
        result == null -> stringResource(R.string.mihomo_node_untested)
        result !is ApiResult.Success -> stringResource(R.string.mihomo_node_test_failed)
        failed -> stringResource(R.string.mihomo_timeout)
        else -> stringResource(R.string.mihomo_delay_result, requireNotNull(delay?.delayMilliseconds))
    }
    val latencyColor = when {
        testing -> MaterialTheme.colorScheme.primary
        failed -> MaterialTheme.colorScheme.error
        else -> MaterialTheme.colorScheme.onSurfaceVariant
    }
    Surface(modifier.selectable(selected = selected, enabled = enabled, role = Role.RadioButton, onClick = onSelect),
        shape = MaterialTheme.shapes.small,
        color = if (selected) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainerLow,
        border = BorderStroke(if (selected) 2.dp else 1.dp, if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outlineVariant)) {
        Column(Modifier.heightIn(min = 70.dp).padding(horizontal = 8.dp, vertical = 6.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(name, Modifier.weight(1f), style = MaterialTheme.typography.labelLarge, maxLines = 2, overflow = TextOverflow.Ellipsis,
                    color = if (selected) MaterialTheme.colorScheme.onPrimaryContainer else MaterialTheme.colorScheme.onSurface)
                if (selected) Icon(Icons.Default.Check, contentDescription = stringResource(R.string.mihomo_active),
                    modifier = Modifier.size(16.dp), tint = MaterialTheme.colorScheme.primary)
            }
            Text(latencyText, style = MaterialTheme.typography.labelSmall, color = latencyColor)
        }
    }
}
