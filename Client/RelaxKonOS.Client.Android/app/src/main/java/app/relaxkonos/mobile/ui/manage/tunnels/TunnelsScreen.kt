package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.compose.runtime.saveable.rememberSaveable
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.operations.*
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.delay
import java.text.DateFormat
import java.util.Date

@OptIn(ExperimentalLayoutApi::class)
@Composable fun TunnelsScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val model: TunnelsViewModel = viewModel(); val state = model.state
    val owner = appContainer().activeSession; val epoch = model.sessionEpoch
    val available = owner?.capabilities?.contains(ServerCapabilities.TUNNELS) == true
    val canManage = available && owner?.privilegedOperations == true
    var confirm by remember(owner, epoch) { mutableStateOf<TunnelConfirmation?>(null) }
    var section by rememberSaveable(owner, epoch) { mutableStateOf("overview") }
    var advanced by rememberSaveable(owner, epoch) { mutableStateOf(false) }
    val frpsSection = section == "frps"
    var install by remember(owner, epoch) { mutableStateOf(false) }
    var tokenProfile by remember(owner, epoch) { mutableStateOf<TunnelProfile?>(null) }
    var secret by remember(owner, epoch) { mutableStateOf("") }
    var recover by remember(owner, epoch) { mutableStateOf(false) }
    var operationId by remember(owner, epoch) { mutableStateOf("") }
    var originalIdentified by remember(owner, epoch) { mutableStateOf(false) }
    OperationMessageDialog(if (state.busy) null else state.problemCode?.let { tunnelProblemLabel(it) } ?: if (state.uncertain) stringResource(R.string.tunnels_uncertain) else null, tone = if (state.problemCode == null) StatusTone.Warning else StatusTone.Danger)
    LaunchedEffect(owner, epoch) { if (available) model.refresh() }
    LaunchedEffect(owner, epoch, state.installation?.operationId, state.installation?.state, state.busy, state.installationVerified) {
        if (state.installationVerified && state.installation?.state?.active == true && !state.busy) { delay(1500); model.pollInstall() }
    }
    val facts = (state.facts as? ApiResult.Success)?.value
    val runtime = (state.runtime as? ApiResult.Success)?.value
    val notInstalled = runtime?.state == TunnelRuntimeState.NotInstalled
    LaunchedEffect(owner, epoch, frpsSection, state.selectedId, state.busy, facts?.observedAtMillis) {
        if (!frpsSection && state.selectedId != null && !state.busy && facts?.definitions?.any { it.profileId == state.selectedId && it.state in setOf(TunnelConnectionState.Starting, TunnelConnectionState.Connected) } == true) {
            delay(3000); model.observe()
        }
    }
    BackHandler(section in setOf("profiles", "logs") && state.selectedId != null && state.profileDraft == null && state.definitionDraft == null && !state.busy) { model.select(null) }
    WorkspaceColumn(stringResource(R.string.tunnels_title), onBack, listOf(WorkspaceDestination("overview", R.string.workspace_overview), WorkspaceDestination("profiles", R.string.workspace_tunnels), WorkspaceDestination("runtime", R.string.workspace_runtime), WorkspaceDestination("logs", R.string.workspace_logs), WorkspaceDestination("frps", R.string.frps_server_tab), WorkspaceDestination("records", R.string.tunnels_records)), section, { section = it }, modifier, stateKey = owner to epoch,
        contentSpacing = Spacing.sm, contentPadding = PaddingValues(Spacing.md)) {
        if (!available) { Text(stringResource(R.string.error_capability_missing)); return@WorkspaceColumn }
        if (section == "overview") Text(stringResource(R.string.tunnels_overview_help))
        if (!canManage) Text(stringResource(R.string.tunnels_observer), style = MaterialTheme.typography.bodySmall)
        PageActionRow(refresh = {
            TextButton(enabled = !state.busy, onClick = { if (frpsSection) model.observeFrps() else model.refresh() }) { ActionLabel(R.string.common_refresh) }
        }, actions = {
            if (canManage) {
                if (section in setOf("overview", "profiles")) Button(enabled = !state.busy && state.pending.isEmpty(), onClick = { model.editProfile() }) { Text(stringResource(R.string.tunnels_profile_create)) }
            }
        })
        RefreshProgressIndicator(visible = state.busy)
        if (section != "records" && (state.uncertain || state.pending.isNotEmpty() || state.pendingInstallation || state.installation != null && !state.installationVerified)) TextButton(onClick = { section = "records" }) { Text(stringResource(R.string.tunnels_records_attention)) }
        // A gap-free container prevents retained, hidden tabs from adding blank rows.
        Column(Modifier.fillMaxWidth()) {
        WorkspaceSection(frpsSection) { key(owner, epoch) { ManagedFrpsManager(model, state, canManage, active = frpsSection) } }
WorkspaceSection(section in setOf("overview", "runtime")) {
        TunnelCard {
        Text(stringResource(R.string.tunnels_runtime_title), style = MaterialTheme.typography.titleMedium)
        if (runtime == null) Text(stringResource(R.string.tunnels_unknown)) else {
            if (notInstalled) Text(stringResource(R.string.runtime_install_hint, "FRP")) else {
            ExecutionStatusChip(tunnelRuntimeLabel(runtime.state), runtime.state.wire, task = false)
            runtime.version?.let { Text(stringResource(R.string.tunnels_version_value, it)) }
            TextButton(onClick = { advanced = !advanced }) { Text(stringResource(if (advanced) R.string.tunnels_advanced_hide else R.string.tunnels_advanced)) }
            if (advanced) {
            runtime.previousVersion?.let { Text(stringResource(R.string.tunnels_previous_version, it)) }
            runtime.executablePath?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            Text(stringResource(if (runtime.integrityVerified) R.string.tunnels_integrity_verified else R.string.tunnels_integrity_unverified))
            }
            runtime.problemCode.takeIf(String::isNotBlank)?.let { Text(tunnelProblemLabel(it)) }
            }
        }
        if (canManage) OutlinedButton(enabled = !state.busy && !state.pendingInstallation && state.installation?.state?.active != true, onClick = { install = true }) { Text(if (notInstalled) stringResource(R.string.runtime_install_action, "FRP") else stringResource(R.string.tunnels_runtime_manage)) }
        }
        if (section == "overview") facts?.let { observed -> TunnelCard {
            Text(stringResource(R.string.tunnels_profiles), style = MaterialTheme.typography.titleMedium)
            Text(stringResource(R.string.tunnels_checked, tunnelDate(observed.observedAtMillis)), style = MaterialTheme.typography.bodySmall)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.tunnels_profiles) + " · " + observed.profiles.size)
                Text(stringResource(R.string.workspace_tunnels) + " · " + observed.definitions.size)
            }
            if (observed.profiles.isEmpty()) Text(stringResource(R.string.tunnels_profiles_empty))
        } }
}
        WorkspaceSection(section == "records") {

                state.installation?.let { operation ->
            TunnelCard {
            Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
            if (state.installationVerified) {
                Text(installationKindLabel(operation.kind), style = MaterialTheme.typography.titleMedium)
                ExecutionStatusChip(installationStateLabel(operation.state), operation.state.wire)
                Text(installationStageLabel(operation.stage)); operation.progress?.let { Text(stringResource(R.string.installation_stage_progress, it)) }
                operation.problemCode?.takeIf(String::isNotBlank)?.let { Text(tunnelProblemLabel(it)) }
            } else Text(stringResource(R.string.tunnels_unknown))
            PageActionRow(refresh = {
                TextButton(enabled = !state.busy, onClick = model::pollInstall) { ActionLabel(R.string.common_refresh) }
            })
            if (canManage && state.installationVerified && operation.state.active && operation.cancellable) TextButton(enabled = !state.busy,
                onClick = { confirm = TunnelConfirmation(R.string.tunnels_cancel_install_confirm, operation.operationId, model::cancelInstall) }) { Text(stringResource(R.string.common_cancel)) }
            }
        }
        if (state.pendingInstallation) TunnelCard {
            Text(stringResource(R.string.tunnels_install_pending), color = MaterialTheme.colorScheme.error)
            if (model.hasIntent && canManage) TextButton(enabled = !state.busy, onClick = { confirm = TunnelConfirmation(R.string.tunnels_install_retry_confirm, "FRP", model::retryInstall) }) { ActionLabel(R.string.common_retry) }
        }
        TextButton(enabled = !state.busy, onClick = { operationId = ""; originalIdentified = false; recover = true }) { Text(stringResource(if (notInstalled) R.string.runtime_recovery_tools else R.string.tunnels_install_recover)) }
        state.pending.forEach { pending ->
            TunnelCard {
            Text(stringResource(R.string.tunnels_pending, tunnelMutationLabel(pending.action), if (pending.action.frps) "frps" else pending.target ?: "—"), color = MaterialTheme.colorScheme.error)
            if (pending.action.frps) {
                Text(stringResource(R.string.frps_pending_note))
                TextButton(enabled = !state.busy, onClick = model::observeFrps) { Text(stringResource(R.string.tunnels_inspect)) }
                if (state.frps is ApiResult.Success) TextButton(enabled = !state.busy, onClick = { confirm = TunnelConfirmation(R.string.tunnels_accept_confirm, "frps") { model.accept(pending) } }) { Text(stringResource(R.string.tunnels_accept)) }
            } else {
                Text(stringResource(R.string.tunnels_pending_note))
                TextButton(enabled = !state.busy, onClick = model::observe) { Text(stringResource(R.string.tunnels_inspect)) }
                if (facts != null) TextButton(enabled = !state.busy, onClick = { confirm = TunnelConfirmation(R.string.tunnels_accept_confirm, pending.target ?: pending.profileId ?: "FRP") { model.accept(pending) } }) { Text(stringResource(R.string.tunnels_accept)) }
            }
            }
        }
        state.action?.let { result -> TunnelCard {
            Text(stringResource(R.string.frps_client_tab), style = MaterialTheme.typography.titleSmall)
            if (result is ApiResult.Success) { Text(stringResource(if (result.value.succeeded) R.string.tunnels_action_succeeded else R.string.tunnels_action_failed)); TunnelBadge(tunnelConnectionLabel(result.value.state), result.value.state == TunnelConnectionState.Connected) }
            else Text(stringResource(R.string.tunnels_uncertain))
        } }
        if (state.installation == null && !state.pendingInstallation && state.pending.isEmpty() && state.action == null && state.frpsAction == null && state.frpsAudit == null && state.frpsLogs == null) TunnelCard { Text(stringResource(R.string.tunnels_records_empty)) }
        key(owner, epoch) { ManagedFrpsManager(model, state, canManage, active = false, records = true) }
        }
WorkspaceSection(section in setOf("profiles", "logs")) {
        if (facts == null) Text(stringResource(R.string.tunnels_unavailable)) else BoxWithConstraints(Modifier.fillMaxWidth()) {
            val selected = facts.profiles.firstOrNull { it.id == state.selectedId }
            if (maxWidth >= 600.dp) Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                Column(Modifier.weight(1f)) { ProfileList(facts, state, model) }
                Column(Modifier.weight(2f)) { ProfileDetail(selected, facts, state, model, canManage, section,
                    { message, target, action -> confirm = TunnelConfirmation(message, target, action) }, { tokenProfile = it; secret = it.token.orEmpty() }) }
            } else Column {
                if (state.selectedId == null) ProfileList(facts, state, model) else {
                    TextButton(enabled = !state.busy, onClick = { model.select(null) }) { Text(stringResource(R.string.common_back)) }
                    ProfileDetail(selected, facts, state, model, canManage, section, { message, target, action -> confirm = TunnelConfirmation(message, target, action) }, { tokenProfile = it; secret = it.token.orEmpty() })
                }
            }
        }
}
            }
    }
    if (install) key(owner, epoch) { TunnelInstallEditor(model, onSubmitted = { section = "records" }) { install = false } }
    if (state.frpsDraft != null) key(owner, epoch) { ManagedFrpsEditor(model, state) }
    if (state.profileDraft != null) key(owner, epoch) { TunnelProfileEditor(state, model) }
    if (state.definitionDraft != null) key(owner, epoch) { TunnelDefinitionEditor(state, model) }
    tokenProfile?.let { profile -> AlertDialog(onDismissRequest = { secret = ""; tokenProfile = null }, modifier = Modifier.imePadding(),
        title = { Text(stringResource(R.string.tunnels_token_set)) }, text = { Column {
            Text(profile.name + " · " + profile.id)
            Text(stringResource(R.string.tunnels_token_note))
            OutlinedTextField(secret, { secret = it }, visualTransformation = androidx.compose.ui.text.input.VisualTransformation.None, singleLine = true,
                keyboardOptions = KeyboardOptions(autoCorrectEnabled = false, keyboardType = KeyboardType.Password), label = { Text(stringResource(R.string.tunnels_token)) })
        } }, confirmButton = { Button(enabled = !state.busy && secret.isNotBlank() && secret.length <= 4096 && state.pending.isEmpty(), onClick = {
            val submitted = secret; secret = ""; tokenProfile = null; model.setToken(profile, submitted)
        }) { ActionLabel(R.string.common_save) } }, dismissButton = { TextButton(onClick = { secret = ""; tokenProfile = null }) { Text(stringResource(R.string.common_cancel)) } }) }
    if (recover) AlertDialog(onDismissRequest = { recover = false }, title = { Text(stringResource(R.string.tunnels_install_recover)) }, text = { Column {
        Text(stringResource(R.string.tunnels_install_recover_note))
        if (state.pendingInstallation) TunnelCheck(originalIdentified, !state.busy, R.string.tunnels_install_identify) { originalIdentified = it }
        OutlinedTextField(operationId, { operationId = it }, singleLine = true, label = { Text(stringResource(R.string.certificates_operation_id)) })
    } }, confirmButton = { Button(enabled = !state.busy && (!state.pendingInstallation || originalIdentified) && runCatching { InstallationRoutes.canonicalId(operationId.trim()) }.isSuccess,
        onClick = { model.recoverInstall(operationId, originalIdentified); recover = false }) { ActionLabel(R.string.common_refresh) } }, dismissButton = { TextButton(onClick = { recover = false }) { Text(stringResource(R.string.common_cancel)) } })
    confirm?.let { request -> AlertDialog(onDismissRequest = { confirm = null }, title = { Text(stringResource(R.string.tunnels_confirm)) },
        text = { Column { Text(request.target); Text(stringResource(request.message)) } }, confirmButton = { Button(onClick = { confirm = null; request.action() }) { Text(stringResource(R.string.tunnels_confirm)) } },
        dismissButton = { TextButton(onClick = { confirm = null }) { Text(stringResource(R.string.common_cancel)) } }) }
}
@OptIn(ExperimentalLayoutApi::class)
@Composable private fun ProfileList(facts: TunnelFacts, state: TunnelsState, model: TunnelsViewModel) {
    Text(stringResource(R.string.tunnels_profiles), style = MaterialTheme.typography.titleSmall)
    if (facts.profiles.isEmpty()) Text(stringResource(R.string.tunnels_profiles_empty))
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
    facts.profiles.forEach { profile -> OutlinedCard(enabled = !state.busy, onClick = { model.select(profile.id) }, modifier = Modifier.fillMaxWidth(),
        border = BorderStroke(if (state.selectedId == profile.id) 2.dp else 1.dp, if (state.selectedId == profile.id) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outlineVariant)) {
        Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(profile.name, style = MaterialTheme.typography.titleMedium)
            Text(profile.host + ":" + profile.port, color = MaterialTheme.colorScheme.onSurfaceVariant)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                TunnelBadge(tunnelModeLabel(profile.runtimeMode)); TunnelBadge(tunnelTlsLabel(profile.tls))
                facts.definitions.filter { it.profileId == profile.id }.map { it.state }.distinct().forEach { connection -> TunnelBadge(tunnelConnectionLabel(connection), connection == TunnelConnectionState.Connected) }
            }
        }
    } }
    }
    Text(stringResource(R.string.tunnels_checked, tunnelDate(facts.observedAtMillis)), style = MaterialTheme.typography.bodySmall)
}
@OptIn(ExperimentalLayoutApi::class)
@Composable private fun ProfileDetail(profile: TunnelProfile?, facts: TunnelFacts, state: TunnelsState, model: TunnelsViewModel, canManage: Boolean,
    section: String, confirm: (Int, String, () -> Unit) -> Unit, token: (TunnelProfile) -> Unit) {
    if (profile == null) { Text(stringResource(if (state.selectedId == null) R.string.tunnels_select_profile else R.string.tunnels_missing)); return }
    val enabled = !state.busy && state.pending.isEmpty() && state.installation?.state?.active != true
    Text(profile.name, style = MaterialTheme.typography.titleMedium); Text(profile.id, style = MaterialTheme.typography.bodySmall)
WorkspaceSection(section != "logs") {
    TunnelCard {
    Text(profile.host + ":" + profile.port); Text(tunnelModeLabel(profile.runtimeMode) + " · " + tunnelAuthLabel(profile.auth) + " · " + tunnelTlsLabel(profile.tls))
    profile.externalPath?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
    Text(stringResource(if (profile.tokenConfigured) R.string.tunnels_token_configured else R.string.tunnels_token_absent))
    Text(stringResource(R.string.tunnels_revision, profile.revision)); Text(stringResource(R.string.tunnels_saved_note))
    if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        OutlinedButton(enabled = enabled, onClick = { model.editProfile(profile) }) { Text(stringResource(R.string.tunnels_edit)) }
        if (profile.auth == TunnelAuth.Token) OutlinedButton(enabled = enabled, onClick = { token(profile) }) { Text(stringResource(R.string.tunnels_token_set)) }
        OutlinedButton(enabled = enabled, onClick = { confirm(R.string.tunnels_apply_confirm, profile.name + " · " + profile.id) { model.lifecycle(profile, true) } }) { Text(stringResource(R.string.tunnels_apply)) }
        OutlinedButton(enabled = enabled, onClick = { confirm(R.string.tunnels_stop_confirm, profile.name + " · " + profile.id) { model.lifecycle(profile, false) } }) { Text(stringResource(R.string.tunnels_stop)) }
        TextButton(enabled = enabled && facts.definitions.none { it.profileId == profile.id }, onClick = { confirm(R.string.tunnels_profile_delete_confirm, profile.name + " · " + profile.id) { model.deleteProfile(profile) } }) { ActionLabel(R.string.common_delete) }
        OutlinedButton(enabled = enabled, onClick = { model.editDefinition(profile.id) }) { Text(stringResource(R.string.tunnels_definition_create)) }
    }
    }
    val definitions = facts.definitions.filter { it.profileId == profile.id }
    if (definitions.isEmpty()) Text(stringResource(R.string.tunnels_definitions_empty))
    definitions.forEach { definition ->
        TunnelCard {
        Text(definition.name + " · " + definition.protocol.wire.uppercase(), style = MaterialTheme.typography.titleMedium)
        Text(stringResource(R.string.tunnels_local_help), style = MaterialTheme.typography.bodySmall)
        SelectionContainer { Text(definition.localHost + ":" + definition.localPort + " → " + (definition.remotePort?.let { profile.host + ":" + it } ?: definition.domain.orEmpty())) }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            TunnelBadge(tunnelConnectionLabel(definition.state), definition.state == TunnelConnectionState.Connected)
            TunnelBadge(stringResource(if (definition.enabled) R.string.tunnels_enabled else R.string.tunnels_disabled), definition.enabled)
        }
        Text(stringResource(R.string.tunnels_revision, definition.revision))
        definition.problemCode.takeIf(String::isNotBlank)?.let { Text(tunnelProblemLabel(it), color = MaterialTheme.colorScheme.error) }
        if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            TextButton(enabled = enabled, onClick = { model.editDefinition(profile.id, definition) }) { Text(stringResource(R.string.tunnels_edit)) }
            TextButton(enabled = enabled, colors = ButtonDefaults.textButtonColors(contentColor = MaterialTheme.colorScheme.error), onClick = { confirm(R.string.tunnels_definition_delete_confirm, definition.name + " · " + definition.id) { model.deleteDefinition(definition) } }) { ActionLabel(R.string.common_delete) }
        }
        }
    }
    Text(stringResource(R.string.tunnels_connection_note), style = MaterialTheme.typography.bodySmall)
}
        WorkspaceSection(section == "logs") {
    TextButton(enabled = !state.busy, onClick = { model.logs(profile.id) }) { Text(stringResource(R.string.tunnels_logs)) }
    when (val logs = state.logs) {
        is ApiResult.Success -> {
            Text(stringResource(R.string.tunnels_logs_note), style = MaterialTheme.typography.bodySmall)
            if (logs.value.isEmpty()) Text(stringResource(R.string.tunnels_logs_empty))
            logs.value.takeLast(200).forEach { Text(tunnelDate(it.timestampMillis) + " · " + it.message, style = MaterialTheme.typography.bodySmall) }
            state.logsAtMillis?.let { Text(stringResource(R.string.tunnels_checked, tunnelDate(it))) }
        }
        null -> Unit
        else -> Text(stringResource(R.string.tunnels_unknown))
    }}

}
internal fun tunnelDate(value: Long) = DateFormat.getDateTimeInstance().format(Date(value))

private data class TunnelConfirmation(val message: Int, val target: String, val action: () -> Unit)
