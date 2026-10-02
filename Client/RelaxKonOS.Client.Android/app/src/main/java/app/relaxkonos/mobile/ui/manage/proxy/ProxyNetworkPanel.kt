package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.runtime.saveable.rememberSaveable
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

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun ProxyNetworkPanel(model: ProxyViewModel, canManage: Boolean, ready: Boolean, section: String, confirm: (() -> Unit) -> Unit) {
    val state = model.state; val overview = (state.overview as? ApiResult.Success)?.value
    val settings = (state.settings as? ApiResult.Success)?.value; val recovery = (state.recovery as? ApiResult.Success)?.value
    var editing by remember { mutableStateOf(false) }; var configuringGeo by remember { mutableStateOf(false) }
WorkspaceSection(section == "settings") {
    Text(stringResource(R.string.mihomo_network_title), style = MaterialTheme.typography.titleMedium)
    Text(stringResource(R.string.mihomo_host_network_note))
    if (recovery == null) Text(stringResource(R.string.mihomo_unverified)) else {
        Text(stringResource(if (recovery.recoveryRequired) R.string.mihomo_recovery_required else R.string.mihomo_recovery_clear))
        if (recovery.hasMarker) Text(stringResource(R.string.mihomo_recovery_marker))
        recovery.markerCreatedAtMillis?.let { Text(DateFormat.getDateTimeInstance().format(Date(it))) }
        recovery.problemCode.takeIf(String::isNotBlank)?.let { Text(proxyProblemLabel(it)) }
    }
    overview?.let { Text(proxyTunLabel(it.tunState)) }
    if (canManage) FlowRow {
        if (overview?.supportsTun == true) {
            OutlinedButton(enabled = ready && overview.controllerReachable && overview.managementRouteSafe && overview.activeProfile != null && recovery?.hasMarker == false,
                onClick = { confirm { model.queue(ProxyAction.EnableTun, overview.activeProfile?.id) } }) { Text(stringResource(R.string.mihomo_tun_enable)) }
            OutlinedButton(enabled = ready && recovery?.hasMarker == true, onClick = { confirm { model.queue(ProxyAction.DisableTun) } }) { Text(stringResource(R.string.mihomo_tun_disable)) }
        } else Text(stringResource(R.string.mihomo_tun_unsupported))
        OutlinedButton(enabled = !state.busy && state.pending.none { it.action == ProxyAction.EmergencyDisableTun } &&
            !(state.operation?.kind == ProxyAction.EmergencyDisableTun.kind && state.operation.state.active),
            onClick = { confirm { model.queue(ProxyAction.EmergencyDisableTun) } }) { Text(stringResource(R.string.mihomo_tun_emergency)) }
        OutlinedButton(enabled = ready && settings != null, onClick = { editing = true }) { Text(stringResource(R.string.mihomo_settings)) }
    }
    settings?.let { Text(stringResource(R.string.mihomo_settings_summary, it.mixedPort,
        stringResource(if (it.systemProxyEnabled) R.string.mihomo_on else R.string.mihomo_off), stringResource(if (it.dnsEnabled) R.string.mihomo_on else R.string.mihomo_off))) }
    Text(stringResource(R.string.mihomo_geodata), style = MaterialTheme.typography.titleSmall)
    val geo = (state.geoData as? ApiResult.Success)?.value
    Text(stringResource(when { geo == null -> R.string.mihomo_unverified; geo.configured -> R.string.mihomo_geodata_configured; else -> R.string.mihomo_geodata_missing }))
    geo?.sizeBytes?.let { Text(stringResource(R.string.mihomo_bytes, it)) }
    if (canManage) TextButton(enabled = ready, onClick = { configuringGeo = true }) { Text(stringResource(R.string.mihomo_geodata_select)) }
}
            HorizontalDivider()
    Text(stringResource(R.string.mihomo_diagnostics), style = MaterialTheme.typography.titleMedium)
    TextButton(enabled = !state.busy && overview != null, onClick = model::diagnostics) { Text(stringResource(R.string.common_refresh)) }
    state.diagnosticsAtMillis?.let { Text(stringResource(R.string.mihomo_observed, DateFormat.getDateTimeInstance().format(Date(it))), style = MaterialTheme.typography.bodySmall) }
WorkspaceSection(section == "connections") {
    val traffic = (state.traffic as? ApiResult.Success)?.value
    if (traffic != null && traffic.problemCode.isBlank()) {
        Text(stringResource(R.string.mihomo_traffic_rate, traffic.uploadPerSecond, traffic.downloadPerSecond))
        Text(stringResource(R.string.mihomo_traffic_total, traffic.uploadTotal, traffic.downloadTotal, traffic.memoryBytes))
    } else if (state.traffic != null) Text(stringResource(R.string.mihomo_unverified))
}
        WorkspaceSection(section == "settings") {
    if (overview?.supportsDns == true) {
        val dns = (state.dns as? ApiResult.Success)?.value
        Text(stringResource(R.string.mihomo_dns_status), style = MaterialTheme.typography.titleSmall)
        if (dns != null && dns.problemCode.isBlank()) Text(stringResource(R.string.mihomo_dns_summary,
            stringResource(if (dns.enabled) R.string.mihomo_on else R.string.mihomo_off), stringResource(if (dns.hijackEnabled) R.string.mihomo_on else R.string.mihomo_off), dns.mode ?: "—"))
        else Text(stringResource(R.string.mihomo_unverified))
    }
}
        WorkspaceSection(section == "connections") {
    if (overview?.supportsConnections == true) {
        Text(stringResource(R.string.mihomo_connections), style = MaterialTheme.typography.titleSmall)
        val connections = (state.connections as? ApiResult.Success)?.value
        if (connections == null) Text(stringResource(R.string.mihomo_unverified)) else connections.take(200).forEach { connection ->
            Text(connection.network + " · " + connection.source + " → " + connection.destination)
            Text(connection.rule + " · " + connection.chains, style = MaterialTheme.typography.bodySmall)
            if (canManage) TextButton(enabled = ready, onClick = { confirm { model.closeConnection(connection.id) } }) { Text(stringResource(R.string.mihomo_connection_close)) }
        }
        if (connections != null && connections.size > 200) Text(stringResource(R.string.mihomo_connections_bounded))
    }
}
        WorkspaceSection(section == "logs") {
    if (overview?.supportsLogs == true) {
        Text(stringResource(R.string.mihomo_logs), style = MaterialTheme.typography.titleSmall)
        val logs = (state.logs as? ApiResult.Success)?.value
        if (logs == null) Text(stringResource(R.string.mihomo_unverified)) else logs.forEach { log ->
            Text(DateFormat.getTimeInstance().format(Date(log.timestampMillis)) + " · " + log.level + " · " + log.message, style = MaterialTheme.typography.bodySmall)
        }
    }
}
            if (editing && settings != null && overview != null) ProxySettingsEditor(settings, overview, model) { editing = false }
    if (configuringGeo) ProxyGeoDataEditor(model) { configuringGeo = false }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable private fun ProxySettingsEditor(original: ProxySettings, overview: ProxyOverview, model: ProxyViewModel, dismiss: () -> Unit) {
    var draft by remember { mutableStateOf(original) }; var port by remember { mutableStateOf(original.mixedPort.toString()) }
    var mtu by remember { mutableStateOf(original.tun?.mtu?.toString().orEmpty()) }
    var interval by remember { mutableStateOf(original.systemProxy?.guardIntervalSeconds?.toString().orEmpty()) }
    var confirmed by remember { mutableStateOf(false) }; var discard by remember { mutableStateOf(false) }
    val initial = remember { model.state.savedEpoch }; val state = model.state
    LaunchedEffect(state.savedEpoch) { if (initial != state.savedEpoch) dismiss() }
    val close = { if (draft != original || port != original.mixedPort.toString() || mtu != original.tun?.mtu?.toString().orEmpty() || interval != original.systemProxy?.guardIntervalSeconds?.toString().orEmpty()) discard = true else dismiss() }
    val windows = overview.operatingSystem?.contains("Windows", true) == true
    val valid = port.toIntOrNull() in 1..65535 && (draft.tun == null || mtu.toIntOrNull() in 576..9000 && draft.tun!!.deviceName.matches(Regex("[A-Za-z0-9_.-]{1,64}")) && (!draft.tun!!.strictRoute || draft.tun!!.autoRoute)) &&
        (draft.systemProxy == null || interval.toIntOrNull() in 5..3600 && draft.systemProxy!!.bypassList.length <= 4096 && draft.systemProxy!!.bypassList.none(Char::isISOControl))
    AlertDialog(onDismissRequest = { if (!state.busy) close() }, modifier = Modifier.imePadding(), title = { Text(stringResource(R.string.mihomo_settings)) }, text = {
        Column(Modifier.heightIn(max = 450.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.mihomo_settings_note))
            ProxyText(port, !state.busy, R.string.mihomo_mixed_port) { port = it }
            ProxyCheck(draft.systemProxyEnabled, !state.busy && (windows || draft.systemProxyEnabled), R.string.mihomo_system_proxy) { draft = draft.copy(systemProxyEnabled = it) }
            if (!windows) Text(stringResource(R.string.mihomo_system_proxy_unsupported))
            ProxyText(draft.systemProxyHost, !state.busy && windows, R.string.mihomo_system_host) { draft = draft.copy(systemProxyHost = it) }
            ProxyCheck(draft.allowLan, !state.busy, R.string.mihomo_allow_lan) { draft = draft.copy(allowLan = it) }
            ProxyCheck(draft.dnsEnabled, !state.busy, R.string.mihomo_dns_enabled) { draft = draft.copy(dnsEnabled = it) }
            ProxyCheck(draft.ipv6Enabled, !state.busy, R.string.mihomo_ipv6) { draft = draft.copy(ipv6Enabled = it) }
            ProxyCheck(draft.unifiedDelay, !state.busy, R.string.mihomo_unified_delay) { draft = draft.copy(unifiedDelay = it) }
            ProxyCheck(draft.allowInsecureSubscriptionSources, !state.busy, R.string.mihomo_insecure_sources) { draft = draft.copy(allowInsecureSubscriptionSources = it) }
            Text(stringResource(R.string.mihomo_log_level)); FlowRow { listOf("silent", "error", "warning", "info", "debug").forEach { level -> FilterChip(draft.logLevel == level, { draft = draft.copy(logLevel = level) }, enabled = !state.busy, label = { Text(proxyLogLevelLabel(level)) }) } }
            draft.tun?.let { tun ->
                Text(stringResource(R.string.mihomo_tun_options)); FlowRow { listOf("system", "gvisor", "mixed").forEach { stack -> FilterChip(tun.stack == stack, { draft = draft.copy(tun = tun.copy(stack = stack)) }, enabled = !state.busy && overview.supportsTun, label = { Text(stack) }) } }
                ProxyText(tun.deviceName, !state.busy && overview.supportsTun, R.string.mihomo_tun_device) { draft = draft.copy(tun = tun.copy(deviceName = it)) }
                ProxyText(mtu, !state.busy && overview.supportsTun, R.string.mihomo_mtu) { mtu = it }
                ProxyCheck(tun.autoRoute, !state.busy && overview.supportsAutoRoute, R.string.mihomo_auto_route) { draft = draft.copy(tun = tun.copy(autoRoute = it, strictRoute = if (it) tun.strictRoute else false)) }
                ProxyCheck(tun.strictRoute, !state.busy && overview.supportsAutoRoute && tun.autoRoute, R.string.mihomo_strict_route) { draft = draft.copy(tun = tun.copy(strictRoute = it)) }
                ProxyCheck(tun.autoDetectInterface, !state.busy && overview.supportsTun, R.string.mihomo_auto_detect) { draft = draft.copy(tun = tun.copy(autoDetectInterface = it)) }
                ProxyText(tun.dnsHijack, !state.busy && overview.supportsDnsHijack, R.string.mihomo_dns_hijack) { draft = draft.copy(tun = tun.copy(dnsHijack = it)) }
            }
            draft.systemProxy?.let { options ->
                ProxyCheck(options.usePac, !state.busy && windows, R.string.mihomo_pac) { draft = draft.copy(systemProxy = options.copy(usePac = it)) }
                ProxyCheck(options.guardEnabled, !state.busy && windows, R.string.mihomo_guard) { draft = draft.copy(systemProxy = options.copy(guardEnabled = it)) }
                ProxyText(interval, !state.busy && windows, R.string.mihomo_guard_interval) { interval = it }
                ProxyCheck(options.useDefaultBypass, !state.busy && windows, R.string.mihomo_default_bypass) { draft = draft.copy(systemProxy = options.copy(useDefaultBypass = it)) }
                ProxyText(options.bypassList, !state.busy && windows, R.string.mihomo_bypass) { draft = draft.copy(systemProxy = options.copy(bypassList = it)) }
            }
            ProxyCheck(confirmed, !state.busy, R.string.mihomo_apply_confirm) { confirmed = it }
            state.problemCode?.let { Text(proxyProblemLabel(it), color = MaterialTheme.colorScheme.error) }
            if (state.uncertain) Text(stringResource(R.string.mihomo_uncertain), color = MaterialTheme.colorScheme.error)
        }
    }, confirmButton = { Button(enabled = !state.busy && confirmed && valid && state.pending.isEmpty(), onClick = {
        model.saveSettings(draft.copy(mixedPort = port.toInt(), tun = draft.tun?.copy(mtu = mtu.toInt()), systemProxy = draft.systemProxy?.copy(guardIntervalSeconds = interval.toInt())))
    }) { Text(stringResource(R.string.common_save)) } }, dismissButton = { TextButton(enabled = !state.busy, onClick = close) { Text(stringResource(R.string.common_cancel)) } })
    if (discard) AlertDialog(onDismissRequest = { discard = false }, text = { Text(stringResource(R.string.mihomo_discard)) }, confirmButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.common_close)) } }, dismissButton = { TextButton(onClick = { discard = false }) { Text(stringResource(R.string.common_cancel)) } })
}
@Composable private fun ProxyGeoDataEditor(model: ProxyViewModel, dismiss: () -> Unit) {
    var path by remember { mutableStateOf("") }; var confirmed by remember { mutableStateOf(false) }; val initial = remember { model.state.savedEpoch }
    LaunchedEffect(model.state.savedEpoch) { if (initial != model.state.savedEpoch) dismiss() }
    AlertDialog(onDismissRequest = { if (!model.state.busy) dismiss() }, modifier = Modifier.imePadding(), title = { Text(stringResource(R.string.mihomo_geodata_select)) }, text = {
        Column(Modifier.heightIn(max = 400.dp).verticalScroll(rememberScrollState())) {
            Text(stringResource(R.string.mihomo_geodata_note)); RemotePathField(path, { path = it }, R.string.mihomo_geodata_path, RemotePathKind.File)
            ProxyCheck(confirmed, !model.state.busy, R.string.mihomo_apply_confirm) { confirmed = it }
            model.state.problemCode?.let { Text(proxyProblemLabel(it), color = MaterialTheme.colorScheme.error) }
            if (model.state.uncertain) Text(stringResource(R.string.mihomo_uncertain), color = MaterialTheme.colorScheme.error)
        }
    }, confirmButton = { Button(enabled = !model.state.busy && confirmed && path.isNotBlank() && model.state.pending.isEmpty(), onClick = { model.configureGeoData(path) }) { Text(stringResource(R.string.common_save)) } }, dismissButton = { TextButton(enabled = !model.state.busy, onClick = dismiss) { Text(stringResource(R.string.common_cancel)) } })
}
