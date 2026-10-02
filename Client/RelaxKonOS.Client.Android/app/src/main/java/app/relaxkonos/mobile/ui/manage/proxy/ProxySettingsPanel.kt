package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.ManagementCard
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

internal enum class ProxySettingsSection(val title: Int) {
    Mihomo(R.string.mihomo_general_settings),
    Tun(R.string.mihomo_tun_settings),
    SystemProxy(R.string.mihomo_system_proxy_settings)
}

@Composable internal fun ProxySettingsPanel(
    state: ProxyState,
    canManage: Boolean,
    ready: Boolean,
    edit: (ProxySettingsSection) -> Unit,
    configureGeo: () -> Unit,
    toggleTun: (Boolean) -> Unit,
    toggleSystemProxy: (Boolean) -> Unit,
    emergency: () -> Unit,
    refreshDns: () -> Unit
) {
    val overview = (state.overview as? ApiResult.Success)?.value
    val settings = (state.settings as? ApiResult.Success)?.value
    val recovery = (state.recovery as? ApiResult.Success)?.value
    val windows = overview?.operatingSystem?.contains("Windows", true) == true
    var diagnosticsExpanded by rememberSaveable { mutableStateOf(false) }

    ManagementCard {
        Text(stringResource(R.string.mihomo_geodata), style = MaterialTheme.typography.titleMedium)
        val geo = (state.geoData as? ApiResult.Success)?.value
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(when {
                geo == null -> R.string.mihomo_unverified
                geo.configured -> R.string.mihomo_geodata_configured
                else -> R.string.mihomo_geodata_missing
            }), Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
            geo?.sizeBytes?.let {
                Text(stringResource(R.string.mihomo_bytes, it), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        if (canManage) TextButton(enabled = ready, onClick = configureGeo) {
            Text(stringResource(R.string.mihomo_geodata_select))
        }
    }
    ManagementCard {
        Text(stringResource(R.string.mihomo_system_settings), style = MaterialTheme.typography.titleMedium)
        ProxyNetworkSettingRow(
            title = R.string.mihomo_tun_mode,
            summary = if (overview?.supportsTun == true) proxyTunLabel(overview.tunState)
                else stringResource(if (overview == null) R.string.mihomo_unverified else R.string.mihomo_tun_unsupported),
            checked = overview?.tunState == "enabled",
            enabled = canManage && ready && overview?.supportsTun == true &&
                (if (overview.tunState == "enabled") recovery?.hasMarker == true else overview.controllerReachable &&
                    overview.managementRouteSafe && overview.activeProfile != null && recovery?.hasMarker == false),
            change = toggleTun,
            editTitle = ProxySettingsSection.Tun.title,
            canEdit = canManage && ready && settings?.tun != null && overview?.supportsTun == true,
            edit = { edit(ProxySettingsSection.Tun) }
        )
        HorizontalDivider()
        ProxyNetworkSettingRow(
            title = R.string.mihomo_system_proxy_title,
            summary = if (windows || settings?.systemProxyEnabled == true) stringResource(when (settings?.systemProxyEnabled) {
                true -> R.string.mihomo_on; false -> R.string.mihomo_off; null -> R.string.mihomo_unverified
            }) else stringResource(if (overview == null) R.string.mihomo_unverified else R.string.mihomo_system_proxy_unsupported),
            checked = settings?.systemProxyEnabled == true,
            enabled = canManage && ready && settings != null && (windows || settings.systemProxyEnabled),
            change = toggleSystemProxy,
            editTitle = ProxySettingsSection.SystemProxy.title,
            canEdit = canManage && ready && settings != null && windows,
            edit = { edit(ProxySettingsSection.SystemProxy) }
        )
        if (recovery == null || recovery.recoveryRequired || recovery.problemCode.isNotBlank()) {
            Text(if (recovery == null) stringResource(R.string.mihomo_unverified)
                else stringResource(R.string.mihomo_recovery_required), color = MaterialTheme.colorScheme.error)
            recovery?.problemCode?.takeIf(String::isNotBlank)?.let {
                Text(proxyProblemLabel(it), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }
        }
        // A failed transition can leave a marker while TUN is no longer reported as enabled.
        if (canManage && recovery?.hasMarker == true && overview?.tunState != "enabled") {
            TextButton(enabled = ready && overview?.supportsTun == true, onClick = { toggleTun(false) }) {
                Text(stringResource(R.string.mihomo_tun_disable))
            }
        }
        if (canManage) TextButton(
            enabled = !state.busy && state.pending.none { it.action == ProxyAction.EmergencyDisableTun } &&
                !(state.operation?.kind == ProxyAction.EmergencyDisableTun.kind && state.operation.state.active),
            onClick = emergency
        ) { Text(stringResource(R.string.mihomo_tun_emergency), color = MaterialTheme.colorScheme.error) }
    }
    ManagementCard {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Text(stringResource(ProxySettingsSection.Mihomo.title), style = MaterialTheme.typography.titleMedium)
                Text(settings?.let { stringResource(R.string.mihomo_general_summary, it.mixedPort, proxyLogLevelLabel(it.logLevel)) }
                    ?: stringResource(R.string.mihomo_unverified), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (canManage) IconButton(enabled = ready && settings != null && overview != null,
                onClick = { edit(ProxySettingsSection.Mihomo) }) {
                Icon(Icons.Default.Settings, contentDescription = stringResource(ProxySettingsSection.Mihomo.title))
            }
        }
    }
    if (overview?.supportsDns == true) ManagementCard {
        TextButton(onClick = { diagnosticsExpanded = !diagnosticsExpanded }) {
            Text(stringResource(if (diagnosticsExpanded) R.string.mihomo_diagnostics_hide else R.string.mihomo_diagnostics_show))
        }
        if (diagnosticsExpanded) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.mihomo_dns_status), Modifier.weight(1f), style = MaterialTheme.typography.titleSmall)
                TextButton(enabled = !state.busy && !state.diagnosticsBusy && overview.controllerReachable, onClick = refreshDns) {
                    if (state.diagnosticsBusy) {
                        CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(Spacing.sm))
                    }
                    Text(stringResource(R.string.common_refresh))
                }
            }
            val dns = (state.dns as? ApiResult.Success)?.value
            if (!overview.controllerReachable) Text(stringResource(R.string.mihomo_controller_unavailable), color = MaterialTheme.colorScheme.error)
            else if (dns != null && dns.problemCode.isBlank()) Text(stringResource(R.string.mihomo_dns_summary,
                stringResource(if (dns.enabled) R.string.mihomo_on else R.string.mihomo_off),
                stringResource(if (dns.hijackEnabled) R.string.mihomo_on else R.string.mihomo_off), dns.mode ?: "—"),
                style = MaterialTheme.typography.bodyMedium)
            else Text(dns?.problemCode?.takeIf(String::isNotBlank)?.let { proxyProblemLabel(it) }
                ?: stringResource(R.string.mihomo_unverified))
            if (state.diagnosticsSection == "settings") state.diagnosticsAtMillis?.let {
                Text(stringResource(R.string.mihomo_observed, DateFormat.getDateTimeInstance().format(Date(it))),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

@Composable private fun ProxyNetworkSettingRow(
    title: Int, summary: String, checked: Boolean, enabled: Boolean, change: (Boolean) -> Unit,
    editTitle: Int, canEdit: Boolean, edit: () -> Unit
) {
    val label = stringResource(title)
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
            Text(label, style = MaterialTheme.typography.bodyLarge)
            Text(summary, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        IconButton(enabled = canEdit, onClick = edit) {
            Icon(Icons.Default.Settings, contentDescription = stringResource(editTitle))
        }
        Switch(checked = checked, onCheckedChange = change, enabled = enabled,
            modifier = Modifier.semantics { contentDescription = label })
    }
}
