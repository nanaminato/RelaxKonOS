package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.TunnelMutation
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.delay

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun ManagedFrpsManager(model: TunnelsViewModel, state: TunnelsState, canManage: Boolean, active: Boolean, records: Boolean = false) {
    var confirm by remember { mutableStateOf<Triple<Int, String, () -> Unit>?>(null) }
    var advanced by remember { mutableStateOf(false) }
    val current = (state.frps as? ApiResult.Success)?.value
    LaunchedEffect(active, current?.state, state.frpsAtMillis, state.busy, state.frpsDraft == null) {
        if (active && current?.state?.active == true && !state.busy && state.frpsDraft == null) { delay(3000); model.observeFrps() }
    }
    if (!records) {
    Text(stringResource(R.string.frps_intro))
    TextButton(enabled = !state.busy, onClick = model::observeFrps) { Text(stringResource(R.string.common_refresh)) }
    if (current == null) Text(stringResource(R.string.tunnels_unknown)) else {
        TunnelCard {
        Text(stringResource(R.string.frps_server_tab), style = MaterialTheme.typography.titleMedium)
        TunnelBadge(frpsStateLabel(current.state), current.state.active)
        Text(current.bindAddress + ":" + current.bindPort)
        Text(stringResource(when {
            current.state == ManagedFrpsState.Unknown -> R.string.frps_process_unverified
            !current.state.active -> R.string.frps_saved_stopped
            current.appliedRevision == null -> R.string.frps_applied_unknown
            current.appliedRevision != current.revision -> R.string.frps_not_applied
            else -> R.string.frps_applied_verified
        }))
        state.frpsAtMillis?.let { Text(stringResource(R.string.tunnels_checked, tunnelDate(it)), style = MaterialTheme.typography.bodySmall) }
        current.problemCode.takeIf(String::isNotBlank)?.let { Text(tunnelProblemLabel(it), color = MaterialTheme.colorScheme.error) }
        if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            val enabled = !state.busy && state.pending.isEmpty() && state.installation?.state?.active != true
            Button(enabled = enabled, onClick = model::editFrps) { Text(stringResource(R.string.tunnels_edit)) }
            OutlinedButton(enabled = enabled && !current.state.active && current.revision > 0, onClick = { confirm = Triple(R.string.frps_start_confirm, "frps · " + current.bindAddress + ":" + current.bindPort) { model.lifecycleFrps(TunnelMutation.StartFrps) } }) { Text(stringResource(R.string.frps_start)) }
            OutlinedButton(enabled = enabled && current.revision > 0, onClick = { confirm = Triple(R.string.frps_stop_confirm, "frps · " + current.bindAddress + ":" + current.bindPort) { model.lifecycleFrps(TunnelMutation.StopFrps) } }) { Text(stringResource(R.string.frps_stop)) }
            OutlinedButton(enabled = enabled && current.revision > 0, onClick = { confirm = Triple(R.string.frps_restart_confirm, "frps · " + current.bindAddress + ":" + current.bindPort) { model.lifecycleFrps(TunnelMutation.RestartFrps) } }) { Text(stringResource(R.string.frps_restart)) }
        }
        }
        TunnelCard {
        Text(stringResource(R.string.tunnels_security), style = MaterialTheme.typography.titleMedium)
        Text(stringResource(R.string.frps_allow_ports) + ": " + current.allowPorts.joinToString(", ") { if (it.start == it.end) it.start.toString() else "${it.start}-${it.end}" })
        current.httpPort?.let { Text(stringResource(R.string.frps_http_value, it)) }
        current.httpsPort?.let { Text(stringResource(R.string.frps_https_value, it)) }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            TunnelBadge(stringResource(if (current.forceTls) R.string.frps_tls_forced else R.string.frps_tls_optional))
            TunnelBadge(stringResource(if (current.tokenConfigured) R.string.tunnels_token_configured else R.string.tunnels_token_absent), current.tokenConfigured)
            TunnelBadge(stringResource(if (current.dashboardEnabled) R.string.frps_dashboard_enabled else R.string.frps_dashboard_disabled), current.dashboardEnabled)
        }
        if (current.dashboardEnabled) {
            Text(current.dashboardAddress + ":" + current.dashboardPort)
            current.dashboardUser?.let { Text(stringResource(R.string.frps_dashboard_user_value, it)) }
            Text(stringResource(if (current.dashboardPasswordConfigured) R.string.frps_password_configured else R.string.frps_password_absent))
        }
        TextButton(onClick = { advanced = !advanced }) { Text(stringResource(if (advanced) R.string.tunnels_advanced_hide else R.string.tunnels_advanced)) }
        if (advanced) {
            Text(stringResource(R.string.frps_revision, current.revision))
            current.appliedRevision?.let { Text(stringResource(R.string.frps_applied_revision, it)) }
            current.startedAtMillis?.let { Text(stringResource(R.string.frps_started, tunnelDate(it))) }
        }
        }
    }
    Text(stringResource(R.string.frps_running_note), style = MaterialTheme.typography.bodySmall)
    }
    if (records) {
    state.frpsAction?.let { action -> when (action) {
        is ApiResult.Success -> TunnelCard { Text(stringResource(R.string.frps_server_tab), style = MaterialTheme.typography.titleSmall); Text(stringResource(if (action.value.succeeded) R.string.tunnels_action_succeeded else R.string.tunnels_action_failed)); TunnelBadge(tunnelConnectionLabel(action.value.state), action.value.state == TunnelConnectionState.Connected) }
        else -> Text(stringResource(R.string.tunnels_uncertain))
    } }
    TunnelCard {
    TextButton(enabled = !state.busy, onClick = model::diagnosticsFrps) { Text(stringResource(R.string.frps_diagnostics)) }
    Text(stringResource(R.string.frps_diagnostics_note), style = MaterialTheme.typography.bodySmall)
    when (val logs = state.frpsLogs) {
        is ApiResult.Success -> {
            Text(stringResource(R.string.tunnels_logs), style = MaterialTheme.typography.titleSmall)
            if (logs.value.isEmpty()) Text(stringResource(R.string.tunnels_logs_empty))
            logs.value.takeLast(200).forEach { Text(tunnelDate(it.timestampMillis) + " · " + it.message, style = MaterialTheme.typography.bodySmall) }
        }
        null -> Unit
        else -> Text(stringResource(R.string.tunnels_unknown))
    }
    when (val audit = state.frpsAudit) {
        is ApiResult.Success -> {
            Text(stringResource(R.string.frps_audit), style = MaterialTheme.typography.titleSmall)
            if (audit.value.isEmpty()) Text(stringResource(R.string.frps_audit_empty))
            audit.value.forEach { entry ->
                Text(tunnelDate(entry.timestampMillis) + " · " + frpsAuditAction(entry.action) + " · " + stringResource(when (entry.result) {
                    "succeeded" -> R.string.tunnels_action_succeeded
                    "failed" -> R.string.tunnels_action_failed
                    else -> R.string.tunnels_unknown
                }), style = MaterialTheme.typography.bodySmall)
                entry.problemCode.takeIf(String::isNotBlank)?.let { Text(tunnelProblemLabel(it)) }
            }
        }
        null -> Unit
        else -> Text(stringResource(R.string.tunnels_unknown))
    }
    state.frpsDiagnosticsAtMillis?.let { Text(stringResource(R.string.tunnels_checked, tunnelDate(it))) }
    }
    }
    confirm?.let { request -> AlertDialog(onDismissRequest = { confirm = null }, title = { Text(stringResource(R.string.tunnels_confirm)) },
        text = { Column { Text(request.second); Text(stringResource(request.first)) } },
        confirmButton = { Button(enabled = !state.busy, onClick = { confirm = null; request.third() }) { Text(stringResource(R.string.tunnels_confirm)) } },
        dismissButton = { TextButton(onClick = { confirm = null }) { Text(stringResource(R.string.common_cancel)) } }) }
}

@Composable internal fun ManagedFrpsEditor(model: TunnelsViewModel, state: TunnelsState) {
    val draft = state.frpsDraft ?: return
    var token by remember { mutableStateOf(charArrayOf()) }; var password by remember { mutableStateOf(charArrayOf()) }
    var currentVisible by remember { mutableStateOf(false) }
    var confirm by remember { mutableStateOf<Int?>(null) }
    val locked = state.busy || state.pending.isNotEmpty()
    fun clear() { token.fill('\u0000'); password.fill('\u0000'); token = charArrayOf(); password = charArrayOf(); currentVisible = false }
    DisposableEffect(model) { onDispose { token.fill('\u0000'); password.fill('\u0000'); model.clearFrpsSecret() } }
    fun close() { if (!state.busy) { if (draft != state.initialFrps || token.isNotEmpty() || password.isNotEmpty()) confirm = R.string.tunnels_discard_confirm else { clear(); model.closeFrps() } } }
    AlertDialog(onDismissRequest = ::close, modifier = Modifier.imePadding(), title = { Text(stringResource(R.string.frps_editor)) },
        text = { Column(Modifier.heightIn(max = 440.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.frps_editor_note)); Text(stringResource(R.string.frps_revision, draft.revision))
            TunnelCard {
            TunnelText(draft.bindAddress, !locked, R.string.frps_bind_address) { model.updateFrps(draft.copy(bindAddress = it)) }
            TunnelText(draft.bindPort, !locked, R.string.frps_bind_port) { model.updateFrps(draft.copy(bindPort = it)) }
            TunnelText(draft.allowPorts, !locked, R.string.frps_allow_ports) { model.updateFrps(draft.copy(allowPorts = it)) }
            Text(stringResource(R.string.frps_allow_ports_note), style = MaterialTheme.typography.bodySmall)
            TunnelText(draft.httpPort, !locked, R.string.frps_http_port) { model.updateFrps(draft.copy(httpPort = it)) }
            TunnelText(draft.httpsPort, !locked, R.string.frps_https_port) { model.updateFrps(draft.copy(httpsPort = it)) }
            }
            TunnelCard {
            Text(stringResource(R.string.tunnels_security), style = MaterialTheme.typography.titleSmall)
            TunnelCheck(draft.forceTls, !locked, R.string.frps_force_tls) { model.updateFrps(draft.copy(forceTls = it)) }
            Text(stringResource(R.string.frps_secret_note), style = MaterialTheme.typography.bodySmall)
            FrpsSecretField(token, !locked, R.string.frps_token_replace) { token.fill('\u0000'); token = it.toCharArray() }
            if (draft.tokenConfigured) TextButton(enabled = !locked, onClick = { confirm = R.string.frps_token_read_confirm }) { Text(stringResource(R.string.frps_token_read)) }
            state.editingToken?.let {
                FrpsSecretField(it, false, R.string.frps_token_current, currentVisible) {}
                TunnelCheck(currentVisible, !state.busy, R.string.frps_show_token) { currentVisible = it }
            }
            }
            TunnelCard {
            TunnelCheck(draft.dashboardEnabled, !locked, R.string.frps_dashboard_enabled) { model.updateFrps(draft.copy(dashboardEnabled = it)) }
            TunnelText(draft.dashboardAddress, !locked, R.string.frps_dashboard_address) { model.updateFrps(draft.copy(dashboardAddress = it)) }
            TunnelText(draft.dashboardPort, !locked, R.string.frps_dashboard_port) { model.updateFrps(draft.copy(dashboardPort = it)) }
            TunnelText(draft.dashboardUser, !locked, R.string.frps_dashboard_user) { model.updateFrps(draft.copy(dashboardUser = it)) }
            FrpsSecretField(password, !locked, R.string.frps_dashboard_password) { password.fill('\u0000'); password = it.toCharArray() }
            Text(stringResource(R.string.frps_dashboard_note), style = MaterialTheme.typography.bodySmall)
            }
            if (draft.request(token, password) == null) Text(stringResource(R.string.frps_invalid), color = MaterialTheme.colorScheme.error)

            if (state.pending.isNotEmpty()) Text(stringResource(R.string.tunnels_uncertain), color = MaterialTheme.colorScheme.error)
            TextButton(enabled = !locked, onClick = { confirm = R.string.tunnels_reload_confirm }) { Text(stringResource(R.string.tunnels_reload)) }
        } }, confirmButton = { Button(enabled = !locked && draft.request(token, password) != null, onClick = { confirm = R.string.frps_save_confirm }) { Text(stringResource(R.string.common_save)) } },
        dismissButton = { TextButton(enabled = !state.busy, onClick = ::close) { Text(stringResource(R.string.common_close)) } })
    confirm?.let { message -> AlertDialog(onDismissRequest = { confirm = null }, title = { Text(stringResource(R.string.tunnels_confirm)) },
        text = { Column { Text("frps · " + draft.bindAddress + ":" + draft.bindPort); Text(stringResource(message)) } }, confirmButton = {
            Button(enabled = !state.busy, onClick = {
                confirm = null
                when (message) {
                    R.string.frps_save_confirm -> { val submittedToken = token.copyOf(); val submittedPassword = password.copyOf(); clear(); model.saveFrps(submittedToken, submittedPassword) }
                    R.string.tunnels_reload_confirm -> { clear(); model.reloadFrpsDraft() }
                    R.string.frps_token_read_confirm -> model.readFrpsToken()
                    else -> { clear(); model.closeFrps() }
                }
            }) { Text(stringResource(R.string.tunnels_confirm)) }
        }, dismissButton = { TextButton(onClick = { confirm = null }) { Text(stringResource(R.string.common_cancel)) } }) }
}
@Composable private fun FrpsSecretField(value: CharArray, enabled: Boolean, label: Int, visible: Boolean = false, change: (String) -> Unit) {
    OutlinedTextField(value.concatToString(), change, enabled = enabled, label = { Text(stringResource(label)) }, modifier = Modifier.fillMaxWidth(), singleLine = true,
        visualTransformation = if (visible) androidx.compose.ui.text.input.VisualTransformation.None else PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(autoCorrectEnabled = false, keyboardType = KeyboardType.Password))
}
