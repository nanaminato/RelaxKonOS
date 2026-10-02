package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

@Composable internal fun ProxyNetworkPanel(model: ProxyViewModel, canManage: Boolean, ready: Boolean, section: String, confirm: (() -> Unit) -> Unit) {
    val state = model.state; val overview = (state.overview as? ApiResult.Success)?.value
    val settings = (state.settings as? ApiResult.Success)?.value
    var editing by remember { mutableStateOf<ProxySettingsSection?>(null) }
    var configuringGeo by remember { mutableStateOf(false) }
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        if (section == "settings") {
            ProxySettingsPanel(state, canManage, ready, edit = { editing = it }, configureGeo = { configuringGeo = true },
                toggleTun = { enabled -> confirm { model.queue(if (enabled) ProxyAction.EnableTun else ProxyAction.DisableTun, if (enabled) overview?.activeProfile?.id else null) } },
                toggleSystemProxy = { enabled -> settings?.let { confirm { model.saveSettings(it.copy(systemProxyEnabled = enabled)) } } },
                emergency = { confirm { model.queue(ProxyAction.EmergencyDisableTun) } },
                refreshDns = { model.diagnostics("settings") })
        } else {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                Text(stringResource(when (section) {
                    "connections" -> R.string.mihomo_connections
                    "logs" -> R.string.mihomo_logs
                    else -> R.string.mihomo_diagnostics
                }), style = MaterialTheme.typography.titleMedium)
                TextButton(enabled = !state.busy && !state.diagnosticsBusy && overview?.controllerReachable == true,
                    onClick = { model.diagnostics(section) }) {
                    if (state.diagnosticsBusy) {
                        CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(Spacing.sm))
                    }
                    Text(stringResource(R.string.common_refresh))
                }
            }
            if (state.diagnosticsSection == section) state.diagnosticsAtMillis?.let {
                Text(stringResource(R.string.mihomo_observed, DateFormat.getDateTimeInstance().format(Date(it))), style = MaterialTheme.typography.bodySmall)
            }
            if (overview?.controllerReachable != true) Text(stringResource(R.string.mihomo_controller_unavailable), color = MaterialTheme.colorScheme.error)
            when (section) {
                "connections" -> {
                    val traffic = (state.traffic as? ApiResult.Success)?.value
                    if (traffic != null && traffic.problemCode.isBlank()) ManagementCard {
                        Text(stringResource(R.string.mihomo_traffic_rate, traffic.uploadPerSecond, traffic.downloadPerSecond))
                        Text(stringResource(R.string.mihomo_traffic_total, traffic.uploadTotal, traffic.downloadTotal, traffic.memoryBytes), style = MaterialTheme.typography.bodySmall)
                    } else if (state.traffic != null) Text(traffic?.problemCode?.takeIf(String::isNotBlank)?.let { proxyProblemLabel(it) }
                        ?: stringResource(R.string.mihomo_unverified), color = MaterialTheme.colorScheme.error)
                    if (overview?.supportsConnections == true) {
                        val connections = (state.connections as? ApiResult.Success)?.value
                        when {
                            connections == null -> Text((state.connections as? ApiResult.Problem)?.code?.let { proxyProblemLabel(it) }
                                ?: stringResource(R.string.mihomo_unverified), color = MaterialTheme.colorScheme.error)
                            connections.isEmpty() -> ManagementCard { Text(stringResource(R.string.mihomo_connections_empty)) }
                            else -> {
                                Text(stringResource(R.string.mihomo_connections_count, connections.size), style = MaterialTheme.typography.labelLarge)
                                connections.take(200).forEach { connection -> ManagementCard {
                                    Row(Modifier.fillMaxWidth(), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                                        Text(connection.network.uppercase() + " · " + connection.destination,
                                            modifier = Modifier.weight(1f), style = MaterialTheme.typography.titleSmall)
                                        if (canManage) TextButton(enabled = ready,
                                            onClick = { confirm { model.closeConnection(connection.id) } }) { Text(stringResource(R.string.mihomo_connection_close)) }
                                    }
                                    Text(connection.source + " → " + connection.destination, style = MaterialTheme.typography.bodySmall)
                                    Text(connection.rule + " · " + connection.chains, style = MaterialTheme.typography.bodySmall)
                                } }
                                if (connections.size > 200) Text(stringResource(R.string.mihomo_connections_bounded))
                            }
                        }
                    }
                }
                "logs" -> if (overview?.supportsLogs == true) {
                    val logs = (state.logs as? ApiResult.Success)?.value
                    if (logs == null) Text((state.logs as? ApiResult.Problem)?.code?.let { proxyProblemLabel(it) }
                        ?: stringResource(R.string.mihomo_unverified))
                    else if (logs.isEmpty()) Text(stringResource(R.string.mihomo_logs_empty))
                    else logs.forEach { log ->
                        Text(DateFormat.getTimeInstance().format(Date(log.timestampMillis)) + " · " + log.level + " · " + log.message, style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
    }
    editing?.let { target ->
        if (settings != null && overview != null) key(target) {
            ProxySettingsEditor(settings, overview, target, model) { editing = null }
        }
    }
    if (configuringGeo) ProxyGeoDataEditor(model) { configuringGeo = false }
}

@Composable private fun ProxyGeoDataEditor(model: ProxyViewModel, dismiss: () -> Unit) {
    var path by remember { mutableStateOf("") }; var confirmed by remember { mutableStateOf(false) }; val initial = remember { model.state.savedEpoch }
    LaunchedEffect(model.state.savedEpoch) { if (initial != model.state.savedEpoch) dismiss() }
    AlertDialog(onDismissRequest = { if (!model.state.busy) dismiss() }, modifier = Modifier.imePadding(), title = { Text(stringResource(R.string.mihomo_geodata_select)) }, text = {
        Column(Modifier.heightIn(max = 400.dp).verticalScroll(rememberScrollState())) {
            Text(stringResource(R.string.mihomo_geodata_note)); RemotePathField(path, { path = it }, R.string.mihomo_geodata_path, RemotePathKind.File)
            ProxyCheck(confirmed, !model.state.busy, R.string.mihomo_apply_confirm) { confirmed = it }

        }
    }, confirmButton = { Button(enabled = !model.state.busy && confirmed && path.isNotBlank() && model.state.pending.isEmpty(), onClick = { model.configureGeoData(path) }) { Text(stringResource(R.string.common_save)) } }, dismissButton = { TextButton(enabled = !model.state.busy, onClick = dismiss) { Text(stringResource(R.string.common_cancel)) } })
}
