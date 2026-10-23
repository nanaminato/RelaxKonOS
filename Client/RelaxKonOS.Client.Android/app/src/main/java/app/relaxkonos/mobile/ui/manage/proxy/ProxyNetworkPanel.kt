package app.relaxkonos.mobile.ui.manage.proxy

import app.relaxkonos.mobile.core.auth.SessionState
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.currentStateAsState
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

private class ProxySettingsEditRequest(val section: ProxySettingsSection, val settings: ProxySettings, val overview: ProxyOverview)

@Composable internal fun ProxyNetworkPanel(model: ProxyViewModel, owner: SessionState.Active?, canManage: Boolean, ready: Boolean, section: String,
    onEditorChanged: (Boolean) -> Unit, confirm: (() -> Unit) -> Unit) {
    val actionKey = model.interactionVersion
    val state = model.state; val overview = (state.overview as? ApiResult.Success)?.value
    val settings = (state.settings as? ApiResult.Success)?.value
    var editing by remember { mutableStateOf<ProxySettingsEditRequest?>(null) }
    var preparingResources by remember { mutableStateOf<Any?>(null) }
    SideEffect { onEditorChanged(editing != null || preparingResources != null) }
    DisposableEffect(Unit) { onDispose { onEditorChanged(false) } }
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        if (section == "settings") {
            ProxySettingsPanel(state, actionKey, canManage, ready, edit = { target ->
                if (owner != null && model.isCurrentInteraction(actionKey, owner) && model.state == state &&
                    !model.state.busy && canManage && ready && editing == null && preparingResources == null && settings != null && overview != null)
                    editing = ProxySettingsEditRequest(target, settings, overview)
            }, prepareResources = {
                if (owner != null && model.isCurrentInteraction(actionKey, owner) && model.state == state &&
                    !model.state.busy && canManage && ready && editing == null && preparingResources == null &&
                    (state.resources as? ApiResult.Success)?.value?.canPrepare == true) preparingResources = Any()
            },
                toggleTun = { enabled -> confirm { model.queue(if (enabled) ProxyAction.EnableTun else ProxyAction.DisableTun, if (enabled) overview?.activeProfile?.id else null) } },
                toggleSystemProxy = { enabled -> settings?.let { confirm { model.saveSettings(it.copy(systemProxyEnabled = enabled,
                    systemProxy = it.systemProxy?.copy(usePac = it.systemProxy.usePac && overview?.systemProxy?.supportsPac == true))) } } },
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
                    ActionLabel(R.string.common_refresh)
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
    editing?.let { request ->
        key(request) {
            val current = { editing === request && owner != null && model.isCurrentInteraction(actionKey, owner) }
            ProxySettingsEditor(request.settings, request.overview, request.section, state, actionKey, current, model::saveSettings) {
                if (current()) editing = null
            }
        }
    }
    preparingResources?.let { request -> key(request) {
        val current = { preparingResources === request && owner != null && model.isCurrentInteraction(actionKey, owner) }
        ProxyResourcesEditor(state, actionKey, current, model::prepareResources) { if (current()) preparingResources = null }
    } }
}

@Composable internal fun ProxyResourcesEditor(state: ProxyState, actionKey: Long, isCurrent: () -> Boolean,
    submit: () -> Unit, dismiss: () -> Unit) {
    val facts = state.resources
    var confirmed by remember(facts) { mutableStateOf(false) }
    var discard by remember { mutableStateOf<Any?>(null) }
    var finished by remember { mutableStateOf(false) }
    var consumed by remember { mutableStateOf<Any?>(null) }
    val initial = remember { state.savedEpoch }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val lifecycleState by lifecycle.currentStateAsState()
    val latestKey by rememberUpdatedState(actionKey)
    val latestState by rememberUpdatedState(state)
    val latestCurrent by rememberUpdatedState(isCurrent)
    val latestDismiss by rememberUpdatedState(dismiss)
    val latestSubmit by rememberUpdatedState(submit)
    fun eligible() = !finished && latestKey == actionKey && latestCurrent() && !latestState.busy &&
        lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
    fun finish() { if (eligible()) { finished = true; discard = null; latestDismiss() } }
    DisposableEffect(Unit) { onDispose { finished = true } }
    LaunchedEffect(actionKey) { discard = null }
    LaunchedEffect(state.savedEpoch, state.busy, actionKey) { if (initial != state.savedEpoch) finish() }
    val requestClose = { if (eligible() && discard == null) { if (confirmed) discard = Any() else finish() } }
    val enabled = !state.busy && !finished && discard == null && lifecycleState.isAtLeast(Lifecycle.State.RESUMED)
    val submission = remember(actionKey, facts, confirmed, state.requestVersion, state.savedEpoch, state.busy, state.pending) { Any() }
    val currentSubmission by rememberUpdatedState(submission)
    AlertDialog(onDismissRequest = requestClose, modifier = Modifier.imePadding(),
        title = { Text(stringResource(R.string.mihomo_geodata_prepare)) }, text = {
            Column(Modifier.heightIn(max = 400.dp).verticalScroll(rememberScrollState())) {
                Text(stringResource(R.string.mihomo_geodata_prepare_note))
                key(actionKey, facts) {
                    ProxyCheck(confirmed, enabled, R.string.mihomo_geodata_prepare_confirm) {
                        if (eligible() && discard == null && latestState.resources == facts) confirmed = it
                    }
                }
                ProxyEditorFeedback(state)
            }
        }, confirmButton = { key(submission) {
            Button(enabled = enabled && confirmed && state.pending.isEmpty() &&
                (facts as? ApiResult.Success)?.value?.canPrepare == true, onClick = {
                if (eligible() && discard == null && currentSubmission === submission && consumed !== submission &&
                    confirmed && latestState.resources == facts && (facts as? ApiResult.Success)?.value?.canPrepare == true &&
                    latestState.pending.isEmpty() && latestState.savedEpoch == initial && latestState.requestVersion == state.requestVersion) {
                    consumed = submission; latestSubmit()
                }
            }) { ActionLabel(R.string.mihomo_geodata_prepare) }
        } }, dismissButton = { key(actionKey) {
            TextButton(enabled = enabled, onClick = requestClose) { Text(stringResource(R.string.common_cancel)) }
        } })
    discard?.let { decision -> key(actionKey, decision) {
        AlertDialog(onDismissRequest = { if (eligible() && discard === decision) discard = null },
            title = { Text(stringResource(R.string.ui_discard_draft_title)) },
            confirmButton = { TextButton(onClick = { if (eligible() && discard === decision) finish() }) {
                Text(stringResource(R.string.editor_discard_changes))
            } }, dismissButton = { TextButton(onClick = { if (eligible() && discard === decision) discard = null }) {
                Text(stringResource(R.string.common_cancel))
            } })
    } }
}
