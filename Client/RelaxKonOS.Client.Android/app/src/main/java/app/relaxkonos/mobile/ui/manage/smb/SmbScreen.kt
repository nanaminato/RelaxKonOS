package app.relaxkonos.mobile.ui.manage.smb

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.operations.*
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.delay
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
    var tab by remember(owner) { mutableIntStateOf(0) }
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
        if (visible && state.installationVerified && state.installation?.state?.active == true && !state.busy) { delay(1500); model.pollInstall() }
    }
    DisposableEffect(owner) { onDispose { model.stop(); password = ""; passwordAgain = "" } }
    BackHandler(draft != null || selected != null) { navigate { draft = null; selected = null } }
    Column(modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(R.string.smb_title), onBack = { navigate(onBack) })
        Text(stringResource(R.string.smb_intro))
        if (owner?.capabilities?.contains(ServerCapabilities.FILE_SERVICES) != true) { Text(stringResource(R.string.error_capability_missing)); return@Column }
        TextButton(enabled = !state.busy, onClick = { navigate { draft = null; model.refresh() } }) { Text(stringResource(R.string.common_refresh)) }
        if (visible && state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        if (visible) state.problem?.let { Text(smbProblem(it), color = MaterialTheme.colorScheme.error) }
        if (visible) state.pending.forEach { pending ->
            Text(stringResource(R.string.smb_pending), color = MaterialTheme.colorScheme.error)
            Text(smbActionLabel(pending.kind)); pending.target?.let { Text(it) }
            pending.receiptId?.let { Text(stringResource(R.string.smb_receipt_id, it), style = MaterialTheme.typography.bodySmall) }
            OutlinedButton(enabled = !state.busy, onClick = { model.accept(pending) }) { Text(stringResource(R.string.smb_accept_facts)) }
        }
        if (facts == null) Text(stringResource(R.string.smb_unverified)) else {
            Text(smbStateLabel(facts.status.state), style = MaterialTheme.typography.titleMedium)
            facts.status.version?.let { Text(it) }
            Text(stringResource(if (facts.status.serviceActive) R.string.smb_service_active else R.string.smb_service_inactive))
            Text(stringResource(if (facts.status.port445Listening) R.string.smb_port_listening else R.string.smb_port_not_listening))
            Text(stringResource(if (facts.capabilities.windowsShareSecuritySupported) R.string.smb_windows_note else R.string.smb_linux_note))
            Text(facts.connection.windowsUncPrefix, style = MaterialTheme.typography.bodySmall)
            Text(facts.connection.smbUriPrefix, style = MaterialTheme.typography.bodySmall)
            state.checkedAtMillis?.let { Text(stringResource(R.string.operations_checked, DateFormat.getDateTimeInstance().format(Date(it)))) }
            facts.capabilities.problemCode?.takeIf(String::isNotBlank)?.let { Text(smbProblem(it)) }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                if (facts.capabilities.installSupported && canManage) OutlinedButton(enabled = !state.busy && draft == null && state.pending.isEmpty() && state.installation?.state?.active != true &&
                    (state.installation == null || state.installationVerified || state.pendingInstallation) &&
                    (facts.status.state == SmbRuntimeState.NotInstalled || state.pendingInstallation), onClick = { install = true }) { Text(stringResource(if (state.pendingInstallation) R.string.common_retry else R.string.smb_install)) }
                if (canManage) SmbChangeKind.entries.filter { it in setOf(SmbChangeKind.Start, SmbChangeKind.Stop, SmbChangeKind.Restart) }.forEach { action ->
                    val allowed = if (action == SmbChangeKind.Start) facts.status.state == SmbRuntimeState.Stopped else facts.status.state == SmbRuntimeState.Running
                    OutlinedButton(enabled = ready && allowed && draft == null, onClick = { confirmation = SmbConfirmation(facts, SmbChange(action)) }) { Text(smbActionLabel(action)) }
                }
            }
        }
        if (visible && state.pendingInstallation) Text(stringResource(R.string.installation_pending, installationServiceLabel(InstallationService.Smb), installationKindLabel(InstallationKind.Install)), color = MaterialTheme.colorScheme.error)
        if (canManage) TextButton(enabled = !state.busy, onClick = { identified = false; recover = true }) { Text(stringResource(R.string.installation_recover)) }
        if (visible) state.installation?.let { operation ->
            Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
            if (state.installationVerified) {
                Text(installationStateLabel(operation.state)); Text(installationStageLabel(operation.stage)); operation.progress?.let { Text(stringResource(R.string.installation_stage_progress, it)) }
                operation.problemCode?.let { Text(installationProblemLabel(it)) }
                if (operation.state.active && operation.cancellable && canManage) OutlinedButton(enabled = !state.busy, onClick = { cancel = true }) { Text(stringResource(R.string.operations_request_cancel)) }
            } else Text(stringResource(R.string.smb_unverified))
        }
        if (visible) state.receipt?.let { Text(stringResource(R.string.smb_receipt_id, it.operationId), style = MaterialTheme.typography.bodySmall); Text(stringResource(R.string.smb_receipt_note)) }
        if (facts != null && facts.capabilities.supported && facts.status.state.manageable) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                FilterChip(tab == 0, { navigate { draft = null; selected = null; tab = 0 } }, label = { Text(stringResource(R.string.smb_shares)) })
                if (facts.capabilities.sambaCredentialsSupported) FilterChip(tab == 1, { navigate { draft = null; selected = null; tab = 1 } }, label = { Text(stringResource(R.string.smb_users)) })
            }
            if (tab == 0 && facts.capabilities.managedSharesSupported) {
                OutlinedButton(enabled = ready && draft == null, onClick = { selected = null; draft = SmbDraft() }) { Text(stringResource(R.string.smb_create)) }
                BoxWithConstraints(Modifier.fillMaxWidth()) {
                    val wide = maxWidth >= 600.dp
                    Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                        if (wide || draft == null && selected == null) Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            if (facts.shares?.isEmpty() == true) Text(stringResource(R.string.smb_no_shares))
                            facts.shares.orEmpty().forEach { share -> ListRow(share.name, subtitle = share.path,
                                supporting = stringResource(if (share.drifted) R.string.smb_drifted else if (!share.managed) R.string.smb_external else if (share.enabled) R.string.smb_enabled else R.string.smb_disabled),
                                selected = selected == share.id, onClick = { navigate { draft = null; selected = share.id } }) }
                        }
                        val editing = draft
                        val chosen = facts.shares?.firstOrNull { it.id == selected }
                        if (editing != null || chosen != null) Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            if (editing != null) SmbShareEditor(editing, facts, ready, { draft = it }, {
                                confirmation = SmbConfirmation(facts, SmbChange(if (editing.id == null) SmbChangeKind.CreateShare else SmbChangeKind.UpdateShare, editing.id, editing.request()))
                            }, { navigate { draft = null } })
                            else if (chosen != null) {
                                Text(chosen.name, style = MaterialTheme.typography.titleMedium); Text(chosen.path); chosen.description?.let { Text(it) }
                                Text(stringResource(if (chosen.readOnly) R.string.smb_read_only else R.string.smb_read_write))
                                Text(stringResource(if (chosen.guestAllowed) R.string.smb_guest else R.string.smb_guest_disabled))
                                chosen.permissions.forEach { Text(it.principal + " · " + smbAccessLabel(it.access)) }
                                if (!chosen.managed || chosen.drifted) Text(stringResource(if (chosen.drifted) R.string.smb_drifted else R.string.smb_external), color = MaterialTheme.colorScheme.error)
                                OutlinedButton(enabled = ready && chosen.managed && !chosen.drifted, onClick = { draft = SmbDraft.from(chosen, facts.capabilities.windowsShareSecuritySupported) }) { Text(stringResource(R.string.smb_edit)) }
                                OutlinedButton(enabled = ready && chosen.managed && !chosen.drifted, onClick = { confirmation = SmbConfirmation(facts, SmbChange(SmbChangeKind.DeleteShare, chosen.id)) }) { Text(stringResource(R.string.common_delete)) }
                                TextButton(onClick = { selected = null }) { Text(stringResource(R.string.common_close)) }
                            }
                        }
                    }
                }
            } else if (tab == 1 && facts.capabilities.sambaCredentialsSupported) {
                Text(stringResource(R.string.smb_users_note))
                if (facts.users?.isEmpty() == true) Text(stringResource(R.string.smb_no_users))
                facts.users.orEmpty().forEach { user ->
                    Text(user.username, style = MaterialTheme.typography.titleMedium)
                    Text(stringResource(if (user.enabled) R.string.smb_user_enabled else R.string.smb_user_disabled))
                    if (!user.eligible) Text(stringResource(R.string.smb_user_ineligible))
                    OutlinedButton(enabled = ready && user.eligible, onClick = { confirmation = SmbConfirmation(facts, SmbChange(if (user.enabled) SmbChangeKind.DisableUser else SmbChangeKind.EnableUser, user.username)) }) { Text(smbActionLabel(if (user.enabled) SmbChangeKind.DisableUser else SmbChangeKind.EnableUser)) }
                    OutlinedButton(enabled = ready && user.eligible, onClick = { password = ""; passwordAgain = ""; confirmation = SmbConfirmation(facts, SmbChange(SmbChangeKind.Password, user.username)) }) { Text(stringResource(R.string.smb_password)) }
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
        confirmButton = { TextButton(onClick = { val action = leave; leave = null; action?.invoke() }) { Text(stringResource(R.string.smb_submit)) } }, dismissButton = { TextButton(onClick = { leave = null }) { Text(stringResource(R.string.common_cancel)) } })
    if (install) AlertDialog(onDismissRequest = { install = false }, title = { Text(stringResource(R.string.smb_install)) }, text = { Text(stringResource(R.string.smb_install_note)) },
        confirmButton = { Button(enabled = !state.busy, onClick = { install = false; model.install() }) { Text(stringResource(R.string.smb_submit)) } }, dismissButton = { TextButton(onClick = { install = false }) { Text(stringResource(R.string.common_cancel)) } })
    if (cancel) AlertDialog(onDismissRequest = { cancel = false }, title = { Text(stringResource(R.string.operations_request_cancel)) }, text = { Text(stringResource(R.string.operations_cancel_explanation)) },
        confirmButton = { Button(onClick = { cancel = false; model.cancelInstall() }) { Text(stringResource(R.string.common_cancel)) } }, dismissButton = { TextButton(onClick = { cancel = false }) { Text(stringResource(R.string.common_close)) } })
    if (recover) AlertDialog(onDismissRequest = { recover = false }, title = { Text(stringResource(R.string.installation_recover)) }, text = { Column {
        Text(stringResource(R.string.installation_recover_help)); OutlinedTextField(operationId, { operationId = it }, singleLine = true, label = { Text(stringResource(R.string.installation_operation_id)) })
        if (state.pendingInstallation) SmbCheck(identified, true, R.string.smb_identify_original) { identified = it }
    } }, confirmButton = { Button(enabled = !state.busy && (!state.pendingInstallation || identified) && runCatching { InstallationRoutes.operation(operationId.trim()) }.isSuccess,
        onClick = { recover = false; model.recoverInstall(operationId, identified) }) { Text(stringResource(R.string.common_refresh)) } }, dismissButton = { TextButton(onClick = { recover = false }) { Text(stringResource(R.string.common_cancel)) } })
}
