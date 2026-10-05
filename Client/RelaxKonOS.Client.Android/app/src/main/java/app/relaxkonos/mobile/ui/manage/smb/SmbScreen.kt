package app.relaxkonos.mobile.ui.manage.smb

import androidx.compose.runtime.saveable.rememberSaveable
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.BorderStroke
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.operations.*
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

private data class SmbConfirmation(val expected: SmbFacts, val change: SmbChange)
@OptIn(ExperimentalLayoutApi::class)
@Composable fun SmbScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val model: SmbViewModel = viewModel(); val owner = appContainer().activeSession; val state = model.state
    val visible = state.owner === owner; val facts = state.facts.takeIf { visible }
    val canManage = visible && owner?.privilegedOperations == true
    val ready = canManage && !state.busy && state.pending.isEmpty() && !state.pendingInstallation && state.installation?.state?.active != true &&
        (state.installation == null || state.installationVerified) && facts?.capabilities?.supported == true && facts.status.state.manageable
    var section by rememberSaveable(owner) { mutableStateOf("overview") }
    var diagnostics by rememberSaveable(owner) { mutableStateOf(false) }
    var selected by remember(owner) { mutableStateOf<String?>(null) }
    var draft by remember(owner) { mutableStateOf<SmbDraft?>(null) }
    var confirmation by remember(owner) { mutableStateOf<SmbConfirmation?>(null) }
    var password by remember(owner) { mutableStateOf("") }; var passwordAgain by remember(owner) { mutableStateOf("") }
    var leave by remember(owner) { mutableStateOf<(() -> Unit)?>(null) }
    var install by remember(owner) { mutableStateOf(false) }; var cancel by remember(owner) { mutableStateOf(false) }
    var recover by remember(owner) { mutableStateOf(false) }; var operationId by remember(owner) { mutableStateOf("") }; var identified by remember(owner) { mutableStateOf(false) }
    val navigate: (() -> Unit) -> Unit = { action -> if (draft != null) leave = action else action() }
    LaunchedEffect(owner, state.owner) { if (visible && owner != null) model.refresh() }
    LaunchedEffect(owner, state.saved) { if (visible && state.saved > 0) draft = null }
    LaunchedEffect(owner, state.installation?.operationId, state.installation?.state, state.installationVerified, state.busy) {
        if (visible) model.observeInstallation()
    }
    DisposableEffect(owner) { onDispose { model.stop(); password = ""; passwordAgain = "" } }
    BackHandler(section == "shares" && (draft != null || selected != null)) { navigate { draft = null; selected = null } }
    val pages = buildList {
        add(WorkspaceDestination("overview", R.string.workspace_overview))
        if (facts?.capabilities?.managedSharesSupported == true) add(WorkspaceDestination("shares", R.string.workspace_shares))
        if (facts?.capabilities?.sambaCredentialsSupported == true) add(WorkspaceDestination("users", R.string.workspace_users))
        add(WorkspaceDestination("records", R.string.smb_records))
    }
    LaunchedEffect(pages) { if (pages.none { it.id == section }) section = "overview" }
    WorkspaceColumn(stringResource(R.string.smb_title), { navigate(onBack) }, pages, section, { destination -> navigate { draft = null; section = destination } }, modifier, stateKey = owner) {
        Text(stringResource(R.string.smb_intro))
        if (!canManage) Text(stringResource(R.string.smb_observer_help), style = MaterialTheme.typography.bodySmall)
        if (owner?.capabilities?.contains(ServerCapabilities.FILE_SERVICES) != true) { Text(stringResource(R.string.error_capability_missing)); return@WorkspaceColumn }
        PageActionRow(refresh = {
            TextButton(enabled = !state.busy, onClick = { navigate { draft = null; model.refresh() } }) { ActionLabel(R.string.common_refresh) }
        })
        RefreshProgressIndicator(visible = visible && state.busy)
        OperationMessageDialog(state.problem?.takeIf { visible && !state.busy }?.let { smbProblem(it) })
        if (visible && section != "records" && (state.pending.isNotEmpty() || state.pendingInstallation || state.installation != null && !state.installationVerified)) {
            TextButton(onClick = { navigate { draft = null; section = "records" } }) { Text(stringResource(R.string.smb_records_attention)) }
        }
        if (visible && section == "records") state.pending.forEach { pending ->
            SmbPanel {
            Text(stringResource(R.string.smb_pending), color = MaterialTheme.colorScheme.error)
            Text(smbActionLabel(pending.kind)); pending.target?.let { Text(it) }
            pending.receiptId?.let { Text(stringResource(R.string.smb_receipt_id, it), style = MaterialTheme.typography.bodySmall) }
            OutlinedButton(enabled = !state.busy, onClick = { model.accept(pending) }) { Text(stringResource(R.string.smb_accept_facts)) }
            }
        }
        if (facts == null && section != "records") Text(stringResource(R.string.smb_unverified))
        if (facts != null) {
WorkspaceSection(section == "overview") {
            SmbPanel {
            Text(stringResource(R.string.smb_title), style = MaterialTheme.typography.titleMedium)
            ExecutionStatusChip(smbStateLabel(facts.status.state), facts.status.state.wire, task = false)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                SmbStatusBadge(stringResource(if (facts.status.serviceActive) R.string.smb_service_active else R.string.smb_service_inactive), facts.status.serviceActive)
                SmbStatusBadge(stringResource(if (facts.status.port445Listening) R.string.smb_port_listening else R.string.smb_port_not_listening), facts.status.port445Listening)
            }
            state.checkedAtMillis?.let { Text(stringResource(R.string.operations_checked, DateFormat.getDateTimeInstance().format(Date(it))), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            TextButton(onClick = { diagnostics = !diagnostics }) { Text(stringResource(if (diagnostics) R.string.smb_advanced_hide else R.string.smb_advanced)) }
            if (diagnostics) {
            facts.status.version?.let { Text(it) }
            Text(stringResource(if (facts.capabilities.windowsShareSecuritySupported) R.string.smb_windows_note else R.string.smb_linux_note))
            }
            facts.status.healthProblemCode?.takeIf(String::isNotBlank)?.let { Text(smbProblem(it), color = MaterialTheme.colorScheme.error) }
            facts.capabilities.problemCode?.takeIf(String::isNotBlank)?.let { Text(smbProblem(it)) }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                if (facts.capabilities.installSupported && canManage && !state.pendingInstallation) OutlinedButton(enabled = !state.busy && draft == null && state.pending.isEmpty() && state.installation?.state?.active != true &&
                    (state.installation == null || state.installationVerified || state.pendingInstallation) &&
                    facts.status.state == SmbRuntimeState.NotInstalled, onClick = { install = true }) { Text(stringResource(R.string.smb_install)) }
                if (canManage) SmbChangeKind.entries.filter { it in setOf(SmbChangeKind.Start, SmbChangeKind.Stop, SmbChangeKind.Restart) }.forEach { action ->
                    val allowed = if (action == SmbChangeKind.Start) facts.status.state == SmbRuntimeState.Stopped else facts.status.state == SmbRuntimeState.Running
                    OutlinedButton(enabled = ready && allowed && draft == null, onClick = { confirmation = SmbConfirmation(facts, SmbChange(action)) }) { Text(smbActionLabel(action)) }
                }
            }
            }
            SmbConnectionCard(facts.connection)
            if (facts.shares != null || facts.users != null) SmbPanel {
                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    facts.shares?.let { shares ->
                        Text(stringResource(R.string.workspace_shares) + " · " + shares.size)
                    }
                    facts.users?.let { users ->
                        Text(stringResource(R.string.workspace_users) + " · " + users.size)
                    }
                }
            }
}
                }
        if (visible && section == "records") {
        Text(stringResource(R.string.smb_records_help), style = MaterialTheme.typography.bodySmall)
        if (state.pendingInstallation) SmbPanel {
            Text(stringResource(R.string.installation_pending, installationServiceLabel(InstallationService.Smb), installationKindLabel(InstallationKind.Install)), color = MaterialTheme.colorScheme.error)
            if (canManage) OutlinedButton(enabled = !state.busy && state.pending.isEmpty() && facts?.capabilities?.installSupported == true && state.installation?.state?.active != true, onClick = { install = true }) { ActionLabel(R.string.common_retry) }
        }
        if (canManage) TextButton(enabled = !state.busy, onClick = { identified = false; recover = true }) { Text(stringResource(R.string.installation_recover)) }
        state.installation?.let { operation ->
            SmbPanel {
            Text(stringResource(R.string.smb_install), style = MaterialTheme.typography.titleMedium)
            Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
            if (state.installationVerified) {
                ExecutionStatusChip(installationStateLabel(operation.state), operation.state.wire); Text(installationStageLabel(operation.stage)); operation.progress?.let { Text(stringResource(R.string.installation_stage_progress, it)) }
                operation.problemCode?.let { Text(installationProblemLabel(it)) }
                if (operation.state.active && operation.cancellable && canManage) OutlinedButton(enabled = !state.busy, onClick = { cancel = true }) { Text(stringResource(R.string.operations_request_cancel)) }
            } else Text(stringResource(R.string.smb_unverified))
            }
        }
        state.receipt?.let { receipt -> SmbPanel {
            Text(stringResource(R.string.smb_receipt_id, receipt.operationId), style = MaterialTheme.typography.titleSmall)
            SmbStatusBadge(installationStateLabel(if (receipt.succeeded) InstallationState.Succeeded else InstallationState.Failed), receipt.succeeded)
            receipt.problemCode?.let { Text(smbProblem(it), color = MaterialTheme.colorScheme.error) }
            Text(stringResource(R.string.smb_receipt_note), style = MaterialTheme.typography.bodySmall)
        } }
        if (state.pending.isEmpty() && !state.pendingInstallation && state.installation == null && state.receipt == null) SmbPanel { Text(stringResource(R.string.smb_records_empty)) }
        }
        if (facts != null && facts.capabilities.supported && facts.status.state.manageable) {
            if (section == "shares" && facts.capabilities.managedSharesSupported) {
                OutlinedButton(enabled = ready && draft == null, onClick = { selected = null; draft = SmbDraft() }) { Text(stringResource(R.string.smb_create)) }
                BoxWithConstraints(Modifier.fillMaxWidth()) {
                    val wide = maxWidth >= 600.dp
                    Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                        if (wide || draft == null && selected == null) Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            if (facts.shares?.isEmpty() == true) SmbPanel { Text(stringResource(R.string.smb_no_shares)) }
                            facts.shares.orEmpty().forEach { share ->
                                OutlinedCard(onClick = { navigate { draft = null; selected = share.id } }, modifier = Modifier.fillMaxWidth(),
                                    border = BorderStroke(if (selected == share.id) 2.dp else 1.dp, if (selected == share.id) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outlineVariant)) {
                                    Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                        Text(share.name, style = MaterialTheme.typography.titleMedium)
                                        Text(share.path, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                            SmbStatusBadge(stringResource(if (share.enabled) R.string.smb_enabled else R.string.smb_disabled), share.enabled)
                                            SmbStatusBadge(stringResource(if (share.readOnly) R.string.smb_read_only else R.string.smb_read_write))
                                            SmbStatusBadge(stringResource(if (share.guestAllowed) R.string.smb_guest else R.string.smb_guest_disabled))
                                        }
                                        if (share.drifted || !share.managed) Text(stringResource(if (share.drifted) R.string.smb_drifted else R.string.smb_external), style = MaterialTheme.typography.bodySmall)
                                    }
                                }
                            }
                        }
                        val editing = draft
                        val chosen = facts.shares?.firstOrNull { it.id == selected }
                        if (editing != null || chosen != null) Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            if (editing != null) SmbShareEditor(editing, facts, ready, { draft = it }, {
                                confirmation = SmbConfirmation(facts, SmbChange(if (editing.id == null) SmbChangeKind.CreateShare else SmbChangeKind.UpdateShare, editing.id, editing.request()))
                            }, { navigate { draft = null } })
                            else if (chosen != null) SmbPanel {
                                Text(chosen.name, style = MaterialTheme.typography.titleMedium); SelectionContainer { Text(chosen.path) }; chosen.description?.let { Text(it) }
                                Text(stringResource(if (chosen.enabled) R.string.smb_enabled else R.string.smb_disabled))
                                Text(stringResource(if (chosen.readOnly) R.string.smb_read_only else R.string.smb_read_write))
                                Text(stringResource(if (chosen.guestAllowed) R.string.smb_guest else R.string.smb_guest_disabled))
                                chosen.permissions.forEach { Text(it.principal + " · " + smbAccessLabel(it.access)) }
                                if (!chosen.managed || chosen.drifted) Text(stringResource(if (chosen.drifted) R.string.smb_drifted else R.string.smb_external), color = MaterialTheme.colorScheme.error)
                                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                    Button(enabled = ready && chosen.managed && !chosen.drifted, onClick = { draft = SmbDraft.from(chosen, facts.capabilities.windowsShareSecuritySupported) }) { Text(stringResource(R.string.smb_edit)) }
                                    OutlinedButton(enabled = ready && chosen.managed && !chosen.drifted, onClick = { confirmation = SmbConfirmation(facts, SmbChange(SmbChangeKind.DeleteShare, chosen.id)) }, colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error)) { ActionLabel(R.string.common_delete) }
                                }
                                TextButton(onClick = { selected = null }) { Text(stringResource(R.string.common_close)) }
                            }
                        }
                    }
                }
            } else if (section == "users" && facts.capabilities.sambaCredentialsSupported) {
                Text(stringResource(R.string.smb_users_note))
                if (facts.users?.isEmpty() == true) SmbPanel { Text(stringResource(R.string.smb_no_users)) }
                facts.users.orEmpty().forEach { user ->
                    SmbPanel {
                    Text(user.username, style = MaterialTheme.typography.titleMedium)
                    SmbStatusBadge(stringResource(if (user.enabled) R.string.smb_user_enabled else R.string.smb_user_disabled), user.enabled)
                    if (!user.eligible) Text(stringResource(R.string.smb_user_ineligible))
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    OutlinedButton(enabled = ready && user.eligible, onClick = { confirmation = SmbConfirmation(facts, SmbChange(if (user.enabled) SmbChangeKind.DisableUser else SmbChangeKind.EnableUser, user.username)) }) { Text(smbActionLabel(if (user.enabled) SmbChangeKind.DisableUser else SmbChangeKind.EnableUser)) }
                    OutlinedButton(enabled = ready && user.eligible, onClick = { password = ""; passwordAgain = ""; confirmation = SmbConfirmation(facts, SmbChange(SmbChangeKind.Password, user.username)) }) { Text(stringResource(R.string.smb_password)) }
                    }
                    }
                }
            }
        }
    }
    confirmation?.let { pending -> AlertDialog(onDismissRequest = { confirmation = null; password = ""; passwordAgain = "" },
        title = { Text(stringResource(R.string.smb_confirm)) }, text = { Column(Modifier.heightIn(max = 450.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(smbActionLabel(pending.change.kind)); pending.change.target?.let { Text(it) }
            Text(stringResource(R.string.smb_host_warning))
            pending.change.share?.let { request ->
                Text(request.name); Text(request.path)
                Text(stringResource(if (request.readOnly) R.string.smb_read_only else R.string.smb_read_write))
                Text(stringResource(if (request.enabled) R.string.smb_enabled else R.string.smb_disabled))
                if (request.enabled && SmbValidation.outsideSuggestedRoot(request.path, pending.expected.capabilities.windowsShareSecuritySupported)) Text(stringResource(R.string.smb_path_warning), color = MaterialTheme.colorScheme.error)
                if (request.guestAllowed) Text(stringResource(R.string.smb_guest_note), color = MaterialTheme.colorScheme.error)
                request.permissions.forEach { Text(it.principal + " · " + smbAccessLabel(it.access)) }
            }
            if (pending.change.kind == SmbChangeKind.DeleteShare) Text(stringResource(R.string.smb_delete_note))
            if (pending.change.kind == SmbChangeKind.Password) {
                Text(stringResource(R.string.smb_password_note))
                OutlinedTextField(password, { password = it }, singleLine = true, label = { Text(stringResource(R.string.smb_password)) }, visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
                OutlinedTextField(passwordAgain, { passwordAgain = it }, singleLine = true, label = { Text(stringResource(R.string.smb_password_again)) }, visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
            }
        } }, confirmButton = { Button(enabled = ready && (pending.change.kind != SmbChangeKind.Password || password == passwordAgain && password.length in 12..1024 && password.none(Char::isISOControl)), onClick = {
            val secret = if (pending.change.kind == SmbChangeKind.Password) password.toCharArray() else null
            password = ""; passwordAgain = ""; confirmation = null; model.change(pending.expected, pending.change, secret)
        }) { Text(stringResource(R.string.smb_submit)) } }, dismissButton = { TextButton(onClick = { confirmation = null; password = ""; passwordAgain = "" }) { Text(stringResource(R.string.common_cancel)) } }) }
    if (leave != null) AlertDialog(onDismissRequest = { leave = null }, title = { Text(stringResource(R.string.smb_discard)) }, text = { Text(stringResource(R.string.smb_discard_note)) },
        confirmButton = { TextButton(onClick = { val action = leave; leave = null; action?.invoke() }) { Text(stringResource(R.string.smb_discard_action)) } }, dismissButton = { TextButton(onClick = { leave = null }) { Text(stringResource(R.string.common_cancel)) } })
    if (install) AlertDialog(onDismissRequest = { install = false }, title = { Text(stringResource(R.string.smb_install)) }, text = { Text(stringResource(R.string.smb_install_note)) },
        confirmButton = { Button(enabled = !state.busy, onClick = { install = false; section = "records"; model.install() }) { Text(stringResource(R.string.smb_submit)) } }, dismissButton = { TextButton(onClick = { install = false }) { Text(stringResource(R.string.common_cancel)) } })
    if (cancel) AlertDialog(onDismissRequest = { cancel = false }, title = { Text(stringResource(R.string.operations_request_cancel)) }, text = { Text(stringResource(R.string.operations_cancel_explanation)) },
        confirmButton = { Button(onClick = { cancel = false; model.cancelInstall() }) { Text(stringResource(R.string.common_cancel)) } }, dismissButton = { TextButton(onClick = { cancel = false }) { Text(stringResource(R.string.common_close)) } })
    if (recover) AlertDialog(onDismissRequest = { recover = false }, title = { Text(stringResource(R.string.installation_recover)) }, text = { Column {
        Text(stringResource(R.string.installation_recover_help)); OutlinedTextField(operationId, { operationId = it }, singleLine = true, label = { Text(stringResource(R.string.installation_operation_id)) })
        if (state.pendingInstallation) SmbCheck(identified, true, R.string.smb_identify_original) { identified = it }
    } }, confirmButton = { Button(enabled = !state.busy && (!state.pendingInstallation || identified) && runCatching { InstallationRoutes.operation(operationId.trim()) }.isSuccess,
        onClick = { recover = false; model.recoverInstall(operationId, identified) }) { ActionLabel(R.string.common_refresh) } }, dismissButton = { TextButton(onClick = { recover = false }) { Text(stringResource(R.string.common_cancel)) } })
}

@Composable internal fun SmbPanel(content: @Composable ColumnScope.() -> Unit) {
    OutlinedCard(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm), content = content)
    }
}

@Composable private fun SmbStatusBadge(label: String, active: Boolean = false) {
    Surface(shape = MaterialTheme.shapes.small, color = if (active) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainerHigh,
        contentColor = if (active) MaterialTheme.colorScheme.onPrimaryContainer else MaterialTheme.colorScheme.onSurfaceVariant) {
        Text(label, Modifier.padding(horizontal = Spacing.sm, vertical = 4.dp), style = MaterialTheme.typography.labelLarge)
    }
}

@Suppress("DEPRECATION")
@Composable private fun SmbConnectionCard(connection: SmbConnection) {
    val clipboard = LocalClipboardManager.current
    OutlinedCard(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.smb_connect), style = MaterialTheme.typography.titleMedium)
            Text(stringResource(R.string.smb_connect_help), style = MaterialTheme.typography.bodySmall)
            listOf(R.string.smb_windows_address to connection.windowsUncPrefix, R.string.smb_uri_address to connection.smbUriPrefix).forEach { (label, address) ->
                Text(stringResource(label), style = MaterialTheme.typography.labelLarge)
                SelectionContainer { Text(address) }
                OutlinedButton(onClick = { clipboard.setText(AnnotatedString(address)) }) { Text(stringResource(R.string.smb_copy_address)) }
            }
        }
    }
}
