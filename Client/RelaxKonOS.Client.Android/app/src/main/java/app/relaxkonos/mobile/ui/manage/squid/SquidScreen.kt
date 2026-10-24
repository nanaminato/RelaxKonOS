package app.relaxkonos.mobile.ui.manage.squid

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

@OptIn(ExperimentalLayoutApi::class)
@Composable fun SquidScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val container = appContainer()
    val model: SquidViewModel = viewModel(factory = viewModelFactory { initializer { SquidViewModel(container.session, container.squid) } })
    val owner = container.activeSession
    val state = model.state
    val visible = state.owner === owner
    val snapshot = state.snapshot.takeIf { visible }
    val draft = if (visible) state.draft else SquidDraft()
    val pending = state.pending.takeIf { visible }
    val ready = visible && !state.busy && pending == null
    val manage = ready && owner?.privilegedOperations == true && owner.controller
    val available = manage && snapshot?.status?.installed == true && snapshot.status.supported
    val normal = available && snapshot?.status?.recoveryRequired == false
    var section by rememberSaveable(owner) { mutableStateOf("overview") }
    var packageAction by remember(owner) { mutableStateOf<SquidAction?>(null) }
    var deleteAccount by remember(owner) { mutableStateOf<SquidAccount?>(null) }
    var username by remember(owner) { mutableStateOf("") }
    var password by remember(owner) { mutableStateOf("") }
    var selected by remember(owner) { mutableStateOf<SquidAccount?>(null) }
    var confirmation by remember(owner) { mutableStateOf(false) }
    var leave by remember(owner) { mutableStateOf<(() -> Unit)?>(null) }
    val dirty = visible && (state.draft != state.initial || password.isNotEmpty())
    val navigate: (() -> Unit) -> Unit = { action -> if (dirty || state.busy || pending != null) leave = action else action() }
    LaunchedEffect(owner, state.owner) { if (owner != null && visible) model.refresh() }
    DisposableEffect(owner) { onDispose { password = ""; model.stop() } }
    BackHandler(dirty || state.busy || pending != null) { navigate(onBack) }
    WorkspaceColumn(stringResource(R.string.squid_title), { navigate(onBack) },
        listOf(WorkspaceDestination("overview", R.string.workspace_overview), WorkspaceDestination("access", R.string.squid_access), WorkspaceDestination("configuration", R.string.squid_preview), WorkspaceDestination("records", R.string.workspace_records)),
        section, { section = it }, modifier, stateKey = owner) {
        if (owner?.capabilities?.contains(ServerCapabilities.GATEWAY_PROXY) != true) {
            Text(stringResource(R.string.error_capability_missing)); return@WorkspaceColumn
        }
        Text(stringResource(R.string.squid_intro))
        PageActionRow(refresh = { TextButton(enabled = visible && !state.busy, onClick = model::refresh) { Text(stringResource(R.string.squid_refresh)) } })
        RefreshProgressIndicator(visible && state.busy)
        state.problem?.takeIf { visible }?.let { Text(squidProblem(it), color = MaterialTheme.colorScheme.error) }
        if (!owner.controller) Text(stringResource(R.string.squid_read_only))
        WorkspaceSection(section == "overview") {
            snapshot?.let { facts ->
                val status = facts.status
                ManagementCard {
                    when {
                        !status.supported -> Text(stringResource(R.string.squid_unsupported))
                        !status.installed -> Text(stringResource(R.string.squid_install_required))
                        status.recoveryRequired -> Text(stringResource(R.string.squid_recovery_required), color = MaterialTheme.colorScheme.error)
                        else -> {
                            Text(stringResource(if (status.running) R.string.squid_running else R.string.squid_stopped))
                            Text(stringResource(if (status.enabled) R.string.squid_enabled else R.string.squid_disabled))
                        }
                    }
                    Text(stringResource(R.string.squid_package_note))
                    if (status.packageManagementSupported) {
                        val action = if (status.installed) SquidAction.Uninstall else SquidAction.Install
                        OutlinedButton(enabled = manage && status.supported && !status.recoveryRequired, onClick = { packageAction = action }) { Text(stringResource(action.label())) }
                    } else Text(stringResource(R.string.squid_package_unsupported))
                    Text(stringResource(R.string.squid_revisions, facts.desiredRevision, facts.publishedRevision?.toString() ?: "—"))
                    Text(stringResource(R.string.squid_runtime_note))
                    Text(stringResource(if (facts.matchesPublishedConfiguration) R.string.squid_matching else R.string.squid_unconfirmed))
                    facts.lastProblemCode?.let { Text(squidProblem(it)) }
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        SquidAction.entries.filter { it != SquidAction.Install && it != SquidAction.Uninstall }.forEach { action ->
                            val enabled = if (action == SquidAction.Stop || action == SquidAction.Disable) available else normal && status.managed
                            OutlinedButton(enabled = enabled, onClick = { model.lifecycle(action) }) { Text(stringResource(action.label())) }
                        }
                    }
                }
            }
            if (snapshot == null && !state.busy) Text(stringResource(R.string.squid_unavailable))
            fun update(value: SquidDraft) = model.edit(value)
            SquidField(R.string.squid_bind_address, draft.address, manage) { update(draft.copy(address = it)) }
            SquidField(R.string.squid_port, draft.port, manage) { update(draft.copy(port = it)) }
            SquidToggle(R.string.squid_remote_clients, draft.remote, manage) { update(draft.copy(remote = it)) }
            SquidField(R.string.squid_networks, draft.networks, manage, multiline = true) { update(draft.copy(networks = it)) }
            SquidField(R.string.squid_allowed_domains, draft.allowed, manage, multiline = true) { update(draft.copy(allowed = it)) }
            SquidField(R.string.squid_denied_domains, draft.denied, manage, multiline = true) { update(draft.copy(denied = it)) }
            SquidField(R.string.squid_upstream_host, draft.parent, manage) { update(draft.copy(parent = it)) }
            SquidField(R.string.squid_upstream_port, draft.parentPort, manage) { update(draft.copy(parentPort = it)) }
            SquidToggle(R.string.squid_adopt, draft.adopt, normal) { update(draft.copy(adopt = it)) }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                OutlinedButton(enabled = manage && snapshot != null, onClick = { if (dirty) leave = model::loadSaved else model.loadSaved() }) { Text(stringResource(R.string.squid_load)) }
                OutlinedButton(enabled = manage, onClick = { section = "configuration"; model.preview() }) { Text(stringResource(R.string.squid_preview)) }
                OutlinedButton(enabled = available, onClick = { section = "configuration"; model.validate() }) { Text(stringResource(R.string.squid_validate)) }
                Button(enabled = normal && (snapshot?.status?.managed == true || draft.adopt) && (draft.accessMode == SquidAccessMode.IpWhitelist || (state.accountsKnown && state.accounts.any { it.enabled })), onClick = { confirmation = true }) { Text(stringResource(R.string.squid_apply)) }
            }
        }
        WorkspaceSection(section == "access") {
            SquidAccessMode.entries.forEach { mode ->
                Row(Modifier.fillMaxWidth()) {
                    RadioButton(selected = draft.accessMode == mode, onClick = { model.edit(draft.copy(accessMode = mode)) }, enabled = manage)
                    Text(stringResource(when (mode) {
                        SquidAccessMode.IpWhitelist -> R.string.squid_mode_ip
                        SquidAccessMode.Password -> R.string.squid_mode_password
                        SquidAccessMode.PasswordAndIpWhitelist -> R.string.squid_mode_both
                    }), Modifier.weight(1f).padding(top = 12.dp))
                }
            }
            Text(stringResource(R.string.squid_auth_hint))
            if (draft.accessMode != SquidAccessMode.IpWhitelist && state.accounts.none { it.enabled }) Text(stringResource(R.string.squid_no_accounts), color = MaterialTheme.colorScheme.error)
            val accountManage = normal && state.accountsKnown
            state.accounts.takeIf { visible }?.forEach { account ->
                OutlinedButton(onClick = { selected = account; username = account.username; password = "" }, enabled = ready) {
                    Text(account.username + " · " + stringResource(if (account.enabled) R.string.squid_account_enabled else R.string.squid_account_disabled))
                }
            }
            SquidField(R.string.squid_account_name, username, accountManage) { username = it; selected = null }
            OutlinedTextField(value = password, onValueChange = { password = it }, label = { Text(stringResource(R.string.squid_account_password)) },
                visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(), enabled = accountManage, singleLine = true, modifier = Modifier.fillMaxWidth())
            val validName = Regex("[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}").matches(username)
            val validPassword = password.length in 8..128 && password.none { Character.isISOControl(it) }
            val current = selected?.let { item -> state.accounts.singleOrNull { it.username == item.username } }
            val protected = current?.enabled == true && state.accounts.count { it.enabled } == 1 &&
                (draft.accessMode != SquidAccessMode.IpWhitelist || snapshot?.configuration?.accessMode != SquidAccessMode.IpWhitelist)
            if (protected) Text(stringResource(R.string.squid_last_account))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Button(enabled = accountManage && validName && validPassword && state.accounts.none { it.username == username }, onClick = {
                    model.account(SquidAccountMutation(SquidAccountAction.Create, username, password)); password = ""
                }) { Text(stringResource(R.string.squid_account_create)) }
                OutlinedButton(enabled = accountManage && current != null && validPassword, onClick = {
                    model.account(SquidAccountMutation(SquidAccountAction.SetPassword, requireNotNull(current).username, password)); password = ""
                }) { Text(stringResource(R.string.squid_account_reset)) }
                OutlinedButton(enabled = accountManage && current != null && !protected, onClick = {
                    model.account(SquidAccountMutation(SquidAccountAction.SetEnabled, requireNotNull(current).username, enabled = !current.enabled))
                }) { Text(stringResource(if (current?.enabled == true) R.string.squid_account_disable else R.string.squid_account_enable)) }
                OutlinedButton(enabled = accountManage && current != null && !protected, onClick = { deleteAccount = current }) { Text(stringResource(R.string.squid_account_delete)) }
            }
        }
        WorkspaceSection(section == "configuration") {
            state.validation?.takeIf { visible }?.let { validation ->
                Text(stringResource(if (validation.valid) R.string.squid_validation_ok else R.string.squid_invalid))
                validation.problemCode?.let { Text(squidProblem(it)) }
                androidx.compose.foundation.text.selection.SelectionContainer { Text(validation.diagnostics) }
            }
            androidx.compose.foundation.text.selection.SelectionContainer { Text(state.preview.takeIf { visible } ?: "") }
        }
        WorkspaceSection(section == "records") {
            pending?.let {
                Text(stringResource(R.string.squid_pending))
                androidx.compose.foundation.text.selection.SelectionContainer { Text(it.operationId ?: it.key) }
                Button(enabled = visible && !state.busy, onClick = model::resume) { Text(stringResource(R.string.squid_resume)) }
            }
            state.operation?.takeIf { visible }?.let { operation ->
                Text(stringResource(R.string.squid_operation))
                androidx.compose.foundation.text.selection.SelectionContainer { Text(operation.operationId) }
                Text(stringResource(when (operation.state) {
                    ProxyOperationState.Queued -> R.string.squid_operation_queued; ProxyOperationState.Running -> R.string.squid_operation_running
                    ProxyOperationState.Succeeded -> R.string.squid_operation_succeeded; ProxyOperationState.Failed -> R.string.squid_operation_failed
                    ProxyOperationState.Cancelled -> R.string.squid_operation_cancelled; ProxyOperationState.Interrupted -> R.string.squid_operation_interrupted
                }))
                if (operation.problemCode.isNotEmpty()) Text(squidProblem(operation.problemCode))
            }
        }
        if (pending != null && section != "records") TextButton(onClick = { section = "records" }) { Text(stringResource(R.string.squid_resume)) }
    }
    packageAction?.let { action -> AlertDialog(onDismissRequest = { packageAction = null },
        title = { Text(stringResource(action.label())) }, text = { Text(stringResource(if (action == SquidAction.Install) R.string.squid_install_message else R.string.squid_uninstall_message)) },
        confirmButton = { TextButton(enabled = manage, onClick = { packageAction = null; section = "records"; model.lifecycle(action) }) { Text(stringResource(R.string.squid_confirm)) } },
        dismissButton = { TextButton(onClick = { packageAction = null }) { Text(stringResource(R.string.squid_cancel)) } }) }
    deleteAccount?.let { account -> AlertDialog(onDismissRequest = { deleteAccount = null },
        text = { Text(stringResource(R.string.squid_delete_message, account.username)) },
        confirmButton = { TextButton(enabled = normal, onClick = { deleteAccount = null; model.account(SquidAccountMutation(SquidAccountAction.Delete, account.username)) }) { Text(stringResource(R.string.squid_confirm)) } },
        dismissButton = { TextButton(onClick = { deleteAccount = null }) { Text(stringResource(R.string.squid_cancel)) } }) }
    if (confirmation) AlertDialog(onDismissRequest = { confirmation = false },
        title = { Text(stringResource(R.string.squid_apply_title)) }, text = { Text(stringResource(R.string.squid_apply_message)) },
        confirmButton = { TextButton(enabled = normal, onClick = { confirmation = false; section = "records"; model.apply() }) { Text(stringResource(R.string.squid_confirm)) } },
        dismissButton = { TextButton(onClick = { confirmation = false }) { Text(stringResource(R.string.squid_cancel)) } })
    if (leave != null) AlertDialog(onDismissRequest = { leave = null }, text = { Text(stringResource(R.string.squid_close_message)) },
        confirmButton = { TextButton(onClick = { val action = leave; leave = null; action?.invoke() }) { Text(stringResource(R.string.squid_confirm)) } },
        dismissButton = { TextButton(onClick = { leave = null }) { Text(stringResource(R.string.squid_cancel)) } })
}
@Composable private fun SquidField(label: Int, value: String, enabled: Boolean, multiline: Boolean = false, change: (String) -> Unit) {
    OutlinedTextField(value, change, label = { Text(stringResource(label)) }, enabled = enabled, singleLine = !multiline,
        minLines = if (multiline) 3 else 1, modifier = Modifier.fillMaxWidth())
}
@Composable private fun SquidToggle(label: Int, value: Boolean, enabled: Boolean, change: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth()) { Checkbox(value, change, enabled = enabled); Text(stringResource(label), Modifier.weight(1f).padding(top = 12.dp)) }
}
private fun SquidAction.label(): Int = when (this) {
    SquidAction.Start -> R.string.squid_start; SquidAction.Stop -> R.string.squid_stop; SquidAction.Restart -> R.string.squid_restart
    SquidAction.Install -> R.string.squid_install; SquidAction.Uninstall -> R.string.squid_uninstall
    SquidAction.Reload -> R.string.squid_reload; SquidAction.Enable -> R.string.squid_enable; SquidAction.Disable -> R.string.squid_disable
}
@Composable private fun squidProblem(code: String): String = when (code) {
    "squid.invalid_port" -> stringResource(R.string.squid_invalid_port)
    "squid.invalid", "squid.configuration.invalid", "squid.configuration.parse_failed" -> stringResource(R.string.squid_invalid)
    "squid.recovery.required" -> stringResource(R.string.squid_recovery_required)
    "squid.runtime.unavailable" -> stringResource(R.string.squid_install_required)
    "squid.pending", "squid.publication.unconfirmed" -> stringResource(R.string.squid_pending)
    "squid.unavailable", "squid.helper.unavailable" -> stringResource(R.string.squid_unavailable)
    else -> stringResource(R.string.squid_unavailable) + "\n" + code
}
