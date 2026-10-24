package app.relaxkonos.mobile.ui.manage.proxy

import app.relaxkonos.mobile.ui.common.ActionLabel
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.currentStateAsState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

@Composable internal fun ProxySettingsEditor(
    original: ProxySettings, overview: ProxyOverview, section: ProxySettingsSection,
    state: ProxyState, actionKey: Long, isCurrent: () -> Boolean, submit: (ProxySettings) -> Unit, dismiss: () -> Unit
) {
    var draft by remember { mutableStateOf(original) }
    var port by remember { mutableStateOf(original.mixedPort.toString()) }
    var mtu by remember { mutableStateOf(original.tun?.mtu?.toString().orEmpty()) }
    var interval by remember { mutableStateOf(original.systemProxy?.guardIntervalSeconds?.toString().orEmpty()) }
    // Approval belongs to these exact values, including numeric text before conversion.
    val draftTicket = remember(draft, port, mtu, interval) { Any() }
    val currentDraftTicket by rememberUpdatedState(draftTicket)
    var confirmed by remember(draftTicket) { mutableStateOf(false) }
    var consumed by remember { mutableStateOf<Any?>(null) }
    var disposed by remember { mutableStateOf(false) }
    DisposableEffect(Unit) { onDispose { disposed = true } }
    val latestState by rememberUpdatedState(state)
    var discard by remember { mutableStateOf<Any?>(null) }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val lifecycleState by lifecycle.currentStateAsState()
    val latestKey by rememberUpdatedState(actionKey)
    val latestCurrent by rememberUpdatedState(isCurrent)
    val latestDismiss by rememberUpdatedState(dismiss)
    fun eligible() = !disposed && latestKey == actionKey && latestCurrent() && !latestState.busy &&
        lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
    fun finish() { if (eligible()) { disposed = true; discard = null; latestDismiss() } }
    LaunchedEffect(actionKey) { discard = null }
    val initial = remember { state.savedEpoch }
    LaunchedEffect(state.savedEpoch, state.busy, actionKey) { if (initial != state.savedEpoch) finish() }
    val close = {
        if (eligible() && discard == null) {
            if (draft != original || port != original.mixedPort.toString() ||
                mtu != original.tun?.mtu?.toString().orEmpty() ||
                interval != original.systemProxy?.guardIntervalSeconds?.toString().orEmpty()) discard = Any()
            else finish()
        }
    }
    val systemProxySupported = overview.systemProxy.supported
    val enabled = !state.busy && !disposed && discard == null && lifecycleState.isAtLeast(Lifecycle.State.RESUMED)
    val valid = when (section) {
        ProxySettingsSection.Mihomo -> port.toIntOrNull() in 1..65535
        ProxySettingsSection.Tun -> draft.tun?.let {
            mtu.toIntOrNull() in 576..9000 && it.deviceName.matches(Regex("[A-Za-z0-9_.-]{1,64}")) &&
                (!it.strictRoute || it.autoRoute)
        } == true && overview.supportsTun
        ProxySettingsSection.SystemProxy -> (!draft.systemProxyEnabled || systemProxySupported) && (draft.systemProxy?.let {
            interval.toIntOrNull() in 5..3600 && it.bypassList.length <= 4096 && it.bypassList.none(Char::isISOControl)
        } ?: true)
    }
    val snapshotDraft = draft
    val snapshotPort = port
    val snapshotMtu = mtu
    val snapshotInterval = interval
    val submission = remember(actionKey, draftTicket, confirmed, state.requestVersion, state.savedEpoch) { Any() }
    val currentSubmission by rememberUpdatedState(submission)
    fun change(action: () -> Unit) {
        if (eligible() && discard == null) action()
    }
    AlertDialog(onDismissRequest = { if (enabled) close() }, modifier = Modifier.imePadding(),
        title = { Text(stringResource(section.title)) }, text = {
            Column(Modifier.heightIn(max = 450.dp).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                key(actionKey) {
                    when (section) {
                        ProxySettingsSection.Mihomo -> {
                            ProxyText(draft.systemProxyHost, enabled, R.string.mihomo_system_host) { change { draft = draft.copy(systemProxyHost = it) } }
                            ProxyText(port, enabled, R.string.mihomo_mixed_port) { change { port = it } }
                            ProxySettingsSwitch(draft.allowLan, enabled, R.string.mihomo_allow_lan) { change { draft = draft.copy(allowLan = it) } }
                            ProxySettingsSwitch(draft.dnsEnabled, enabled, R.string.mihomo_dns_enabled) { change { draft = draft.copy(dnsEnabled = it) } }
                            ProxySettingsSwitch(draft.ipv6Enabled, enabled, R.string.mihomo_ipv6) { change { draft = draft.copy(ipv6Enabled = it) } }
                            ProxySettingsSwitch(draft.unifiedDelay, enabled, R.string.mihomo_unified_delay) { change { draft = draft.copy(unifiedDelay = it) } }
                            ProxySettingsChoice(R.string.mihomo_log_level, proxyLogLevelLabel(draft.logLevel), enabled) { closeMenu ->
                                listOf("silent", "error", "warning", "info", "debug").forEach { level ->
                                    DropdownMenuItem(text = { Text(proxyLogLevelLabel(level)) }, onClick = {
                                        change { draft = draft.copy(logLevel = level); closeMenu() }
                                    })
                                }
                            }
                            ProxySettingsSwitch(draft.allowInsecureSubscriptionSources, enabled, R.string.mihomo_insecure_sources) {
                                change { draft = draft.copy(allowInsecureSubscriptionSources = it) }
                            }
                        }
                        ProxySettingsSection.Tun -> {
                            Text(stringResource(R.string.mihomo_host_network_note), style = MaterialTheme.typography.bodySmall)
                            Text(proxyTunLabel(overview.tunState), style = MaterialTheme.typography.labelLarge)
                            val recovery = (state.recovery as? ApiResult.Success)?.value
                            Text(stringResource(when {
                                recovery == null -> R.string.mihomo_unverified
                                recovery.recoveryRequired -> R.string.mihomo_recovery_required
                                else -> R.string.mihomo_recovery_clear
                            }), style = MaterialTheme.typography.bodySmall)
                            if (recovery?.hasMarker == true) {
                                Text(stringResource(R.string.mihomo_recovery_marker), style = MaterialTheme.typography.bodySmall)
                                recovery.markerCreatedAtMillis?.let {
                                    Text(DateFormat.getDateTimeInstance().format(Date(it)), style = MaterialTheme.typography.bodySmall)
                                }
                            }
                            draft.tun?.let { tun ->
                                ProxySettingsChoice(R.string.mihomo_tun_stack, tun.stack, enabled) { closeMenu ->
                                    listOf("system", "gvisor", "mixed").forEach { stack ->
                                        DropdownMenuItem(text = { Text(stack) }, onClick = {
                                            change { draft = draft.copy(tun = draft.tun?.copy(stack = stack)); closeMenu() }
                                        })
                                    }
                                }
                                ProxyText(tun.deviceName, enabled, R.string.mihomo_tun_device) { change { draft = draft.copy(tun = draft.tun?.copy(deviceName = it)) } }
                                ProxyText(mtu, enabled, R.string.mihomo_mtu) { change { mtu = it } }
                                ProxySettingsSwitch(tun.autoRoute, enabled && overview.supportsAutoRoute, R.string.mihomo_auto_route) {
                                    change { draft = draft.copy(tun = draft.tun?.copy(autoRoute = it, strictRoute = if (it) draft.tun?.strictRoute == true else false)) }
                                }
                                ProxySettingsSwitch(tun.strictRoute, enabled && overview.supportsAutoRoute && tun.autoRoute, R.string.mihomo_strict_route) {
                                    change { draft = draft.copy(tun = draft.tun?.copy(strictRoute = it)) }
                                }
                                ProxySettingsSwitch(tun.autoDetectInterface, enabled, R.string.mihomo_auto_detect) {
                                    change { draft = draft.copy(tun = draft.tun?.copy(autoDetectInterface = it)) }
                                }
                                ProxyText(tun.dnsHijack, enabled && overview.supportsDnsHijack, R.string.mihomo_dns_hijack) {
                                    change { draft = draft.copy(tun = draft.tun?.copy(dnsHijack = it)) }
                                }
                            }
                            Text(stringResource(R.string.mihomo_tun_options), style = MaterialTheme.typography.bodySmall)
                        }
                        ProxySettingsSection.SystemProxy -> {
                            Text(stringResource(when {
                                !systemProxySupported -> R.string.mihomo_system_proxy_unsupported
                                overview.systemProxy.loginEnvironment && overview.systemProxy.desktopSession -> R.string.mihomo_system_proxy_linux_desktop_scope
                                overview.systemProxy.loginEnvironment -> R.string.mihomo_system_proxy_linux_scope
                                else -> R.string.mihomo_system_proxy_windows_scope
                            }), style = MaterialTheme.typography.bodySmall)
                            ProxySettingsSwitch(draft.systemProxyEnabled, enabled && (systemProxySupported || draft.systemProxyEnabled), R.string.mihomo_system_proxy) {
                                change { draft = draft.copy(systemProxyEnabled = it) }
                            }
                            Text(stringResource(R.string.mihomo_proxy_endpoint, draft.systemProxyHost, draft.mixedPort),
                                style = MaterialTheme.typography.bodySmall)
                            Text(stringResource(R.string.mihomo_proxy_host_follows_listener), style = MaterialTheme.typography.bodySmall)
                            draft.systemProxy?.let { options ->
                                if (overview.systemProxy.supportsPac) ProxySettingsSwitch(options.usePac, enabled && systemProxySupported, R.string.mihomo_pac) { change { draft = draft.copy(systemProxy = draft.systemProxy?.copy(usePac = it)) } }
                                if (overview.systemProxy.loginEnvironment) Text(stringResource(R.string.mihomo_system_proxy_linux_bypass), style = MaterialTheme.typography.bodySmall)
                                ProxySettingsSwitch(options.guardEnabled, enabled && systemProxySupported, R.string.mihomo_guard) { change { draft = draft.copy(systemProxy = draft.systemProxy?.copy(guardEnabled = it)) } }
                                ProxyText(interval, enabled && systemProxySupported, R.string.mihomo_guard_interval) { change { interval = it } }
                                ProxySettingsSwitch(options.useDefaultBypass, enabled && systemProxySupported, R.string.mihomo_default_bypass) { change { draft = draft.copy(systemProxy = draft.systemProxy?.copy(useDefaultBypass = it)) } }
                                ProxyText(options.bypassList, enabled && systemProxySupported, R.string.mihomo_bypass) { change { draft = draft.copy(systemProxy = draft.systemProxy?.copy(bypassList = it)) } }
                            }
                        }
                    }
                }
                HorizontalDivider()
                Text(stringResource(R.string.mihomo_settings_note), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                key(actionKey, draftTicket) {
                    ProxyCheck(confirmed, enabled, R.string.mihomo_apply_confirm) {
                        if (currentDraftTicket === draftTicket && draft == snapshotDraft && port == snapshotPort &&
                            mtu == snapshotMtu && interval == snapshotInterval) change { confirmed = it }
                    }
                }
                ProxyEditorFeedback(state)
            }
        }, confirmButton = {
            key(submission) {
                Button(enabled = enabled && confirmed && valid && state.pending.isEmpty(), onClick = {
                    if (eligible() && discard == null && currentDraftTicket === draftTicket && currentSubmission === submission && confirmed && valid &&
                        draft == snapshotDraft && port == snapshotPort && mtu == snapshotMtu && interval == snapshotInterval &&
                        !latestState.busy && latestState.pending.isEmpty() && latestState.savedEpoch == initial &&
                        latestState.requestVersion == state.requestVersion && consumed !== submission) {
                        consumed = submission
                        // Submit the current contract while preserving every other group's fields.
                        submit(when (section) {
                            ProxySettingsSection.Mihomo -> draft.copy(mixedPort = port.toInt())
                            ProxySettingsSection.Tun -> draft.copy(tun = draft.tun?.copy(mtu = mtu.toInt()))
                            ProxySettingsSection.SystemProxy -> draft.copy(systemProxy = draft.systemProxy?.copy(guardIntervalSeconds = interval.toInt(),
                                usePac = draft.systemProxy?.usePac == true && overview.systemProxy.supportsPac))
                        })
                    }
                }) { ActionLabel(R.string.common_save) }
            }
        }, dismissButton = { key(actionKey) { TextButton(enabled = enabled, onClick = close) { Text(stringResource(R.string.common_cancel)) } } })
    discard?.let { decision -> key(actionKey, decision) {
        AlertDialog(onDismissRequest = { if (eligible() && discard === decision) discard = null },
            text = { Text(stringResource(R.string.mihomo_discard)) },
            confirmButton = { TextButton(onClick = { if (eligible() && discard === decision) finish() }) { Text(stringResource(R.string.common_close)) } },
            dismissButton = { TextButton(onClick = { if (eligible() && discard === decision) discard = null }) { Text(stringResource(R.string.common_cancel)) } })
    } }
}

@Composable private fun ProxySettingsSwitch(value: Boolean, enabled: Boolean, label: Int, change: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
        Text(stringResource(label), Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
        Switch(checked = value, onCheckedChange = change, enabled = enabled)
    }
}

@Composable private fun ProxySettingsChoice(title: Int, value: String, enabled: Boolean,
    options: @Composable ColumnScope.(close: () -> Unit) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(stringResource(title), Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
        Box {
            TextButton(enabled = enabled, onClick = { expanded = true }) { Text(value) }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                options { expanded = false }
            }
        }
    }
}
