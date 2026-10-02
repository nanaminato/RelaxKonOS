package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.selection.toggleable
import androidx.compose.ui.Alignment
import androidx.compose.ui.semantics.Role
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun TunnelProfileEditor(state: TunnelsState, model: TunnelsViewModel) {
    val draft = state.profileDraft ?: return
    val locked = state.busy || state.pending.isNotEmpty()
    var discard by remember { mutableStateOf(false) }; var save by remember { mutableStateOf(false) }; var reload by remember { mutableStateOf(false) }
    fun close() { if (!state.busy) { if (draft != state.initialProfile) discard = true else model.closeProfile() } }
    AlertDialog(onDismissRequest = ::close, modifier = Modifier.imePadding(), title = { Text(stringResource(R.string.tunnels_profile_editor)) },
        text = { Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.tunnels_saved_note))
            TunnelCard {
            TunnelText(draft.name, !locked, R.string.tunnels_name) { model.updateProfile(draft.copy(name = it)) }
            TunnelText(draft.host, !locked, R.string.tunnels_host) { model.updateProfile(draft.copy(host = it)) }
            TunnelText(draft.port, !locked, R.string.tunnels_server_port) { model.updateProfile(draft.copy(port = it)) }
            }
            TunnelCard {
            Text(stringResource(R.string.tunnels_security), style = MaterialTheme.typography.titleSmall)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) { TunnelAuth.entries.forEach { auth -> FilterChip(draft.auth == auth, { model.updateProfile(draft.copy(auth = auth)) }, enabled = !locked, label = { Text(tunnelAuthLabel(auth)) }) } }
            Text(stringResource(R.string.tunnels_token_editor_note))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) { TunnelTls.entries.forEach { tls -> FilterChip(draft.tls == tls, { model.updateProfile(draft.copy(tls = tls)) }, enabled = !locked, label = { Text(tunnelTlsLabel(tls)) }) } }
            if (draft.tls == TunnelTls.Disable) Text(stringResource(R.string.tunnels_tls_warning), color = MaterialTheme.colorScheme.error)
            }
            TunnelCard {
            Text(stringResource(R.string.tunnels_runtime_title), style = MaterialTheme.typography.titleSmall)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) { TunnelRuntimeMode.entries.forEach { mode -> FilterChip(draft.mode == mode, { model.updateProfile(draft.copy(mode = mode)) }, enabled = !locked, label = { Text(tunnelModeLabel(mode)) }) } }
            if (draft.mode == TunnelRuntimeMode.External) {
                if (!locked) RemotePathField(draft.path, { model.updateProfile(draft.copy(path = it)) }, R.string.tunnels_external_path, RemotePathKind.File) else Text(draft.path)
                Text(stringResource(R.string.tunnels_external_note))
                OutlinedButton(enabled = !locked && TunnelInputs.absolutePath(draft.path.trim()), onClick = { model.detect(draft.path.trim()) }) { Text(stringResource(R.string.tunnels_probe)) }
                when (val detected = state.detected) {
                    is ApiResult.Success -> { Text(tunnelRuntimeLabel(detected.value.state)); detected.value.version?.let { Text(stringResource(R.string.tunnels_version_value, it)) }; detected.value.problemCode.takeIf(String::isNotBlank)?.let { Text(tunnelProblemLabel(it)) } }
                    null -> Unit
                    else -> Text(stringResource(R.string.tunnels_unknown))
                }
            }
            }
            EditorStatus(state, draft.request() == null)
            if (draft.id != null) TextButton(enabled = !locked, onClick = { reload = true }) { Text(stringResource(R.string.tunnels_reload)) }
        } }, confirmButton = { Button(enabled = !locked && draft.request() != null, onClick = { save = true }) { Text(stringResource(R.string.common_save)) } },
        dismissButton = { TextButton(enabled = !state.busy, onClick = ::close) { Text(stringResource(R.string.common_close)) } })
    EditorConfirmation(discard || save || reload, draft.name + " · " + draft.id.orEmpty(), when { save -> R.string.tunnels_profile_save_confirm; reload -> R.string.tunnels_reload_confirm; else -> R.string.tunnels_discard_confirm },
        { discard = false; save = false; reload = false }, { when { save -> model.saveProfile(); reload -> model.reloadDraft(); else -> model.closeProfile() }; discard = false; save = false; reload = false })
}
@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun TunnelDefinitionEditor(state: TunnelsState, model: TunnelsViewModel) {
    val draft = state.definitionDraft ?: return
    val locked = state.busy || state.pending.isNotEmpty()
    val profiles = (state.facts as? ApiResult.Success)?.value?.profiles.orEmpty()
    var discard by remember { mutableStateOf(false) }; var save by remember { mutableStateOf(false) }; var reload by remember { mutableStateOf(false) }
    fun close() { if (!state.busy) { if (draft != state.initialDefinition) discard = true else model.closeDefinition() } }
    AlertDialog(onDismissRequest = ::close, modifier = Modifier.imePadding(), title = { Text(stringResource(R.string.tunnels_definition_editor)) },
        text = { Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.tunnels_saved_note))
            TunnelCard {
            TunnelText(draft.name, !locked, R.string.tunnels_name) { model.updateDefinition(draft.copy(name = it)) }
            Text(stringResource(R.string.tunnels_profiles))
            profiles.forEach { profile -> TextButton(enabled = !locked, onClick = { model.updateDefinition(draft.copy(profileId = profile.id)) }) { Text((if (draft.profileId == profile.id) "✓ " else "") + profile.name) } }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) { TunnelProtocol.entries.forEach { protocol -> FilterChip(draft.protocol == protocol, { model.updateDefinition(draft.copy(protocol = protocol)) }, enabled = !locked, label = { Text(protocol.wire.uppercase()) }) } }
            }
            TunnelCard {
            Text(stringResource(R.string.tunnels_destination), style = MaterialTheme.typography.titleSmall)
            Text(stringResource(R.string.tunnels_local_help), style = MaterialTheme.typography.bodySmall)
            TunnelText(draft.localHost, !locked, R.string.tunnels_local_host) { model.updateDefinition(draft.copy(localHost = it)) }
            TunnelText(draft.localPort, !locked, R.string.tunnels_local_port) { model.updateDefinition(draft.copy(localPort = it)) }
            if (draft.protocol.usesPort) TunnelText(draft.remotePort, !locked, R.string.tunnels_remote_port) { model.updateDefinition(draft.copy(remotePort = it)) }
            else TunnelText(draft.domain, !locked, R.string.tunnels_domain) { model.updateDefinition(draft.copy(domain = it)) }
            }
            TunnelCard {
            TunnelCheck(draft.enabled, !locked, R.string.tunnels_enabled) { model.updateDefinition(draft.copy(enabled = it)) }
            TunnelCheck(draft.encryption, !locked, R.string.tunnels_encryption) { model.updateDefinition(draft.copy(encryption = it)) }
            TunnelCheck(draft.compression, !locked, R.string.tunnels_compression) { model.updateDefinition(draft.copy(compression = it)) }
            }
            EditorStatus(state, draft.request() == null || profiles.none { it.id == draft.profileId })
            if (draft.id != null) TextButton(enabled = !locked, onClick = { reload = true }) { Text(stringResource(R.string.tunnels_reload)) }
        } }, confirmButton = { Button(enabled = !locked && draft.request() != null && profiles.any { it.id == draft.profileId }, onClick = { save = true }) { Text(stringResource(R.string.common_save)) } },
        dismissButton = { TextButton(enabled = !state.busy, onClick = ::close) { Text(stringResource(R.string.common_close)) } })
    EditorConfirmation(discard || save || reload, draft.name + " · " + draft.id.orEmpty(), when { save -> R.string.tunnels_definition_save_confirm; reload -> R.string.tunnels_reload_confirm; else -> R.string.tunnels_discard_confirm },
        { discard = false; save = false; reload = false }, { when { save -> model.saveDefinition(); reload -> model.reloadDraft(); else -> model.closeDefinition() }; discard = false; save = false; reload = false })
}
@Composable private fun EditorStatus(state: TunnelsState, invalid: Boolean) {
    if (invalid) Text(stringResource(R.string.tunnels_validation), color = MaterialTheme.colorScheme.error)

    if (state.pending.isNotEmpty()) Text(stringResource(R.string.tunnels_uncertain), color = MaterialTheme.colorScheme.error)
    if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
}
@Composable private fun EditorConfirmation(visible: Boolean, target: String, message: Int, dismiss: () -> Unit, submit: () -> Unit) {
    if (visible) AlertDialog(onDismissRequest = dismiss, title = { Text(stringResource(R.string.tunnels_confirm)) }, text = { Column { Text(target); Text(stringResource(message)) } },
        confirmButton = { Button(onClick = submit) { Text(stringResource(R.string.tunnels_confirm)) } }, dismissButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.common_cancel)) } })
}
@Composable internal fun TunnelText(value: String, enabled: Boolean, label: Int, change: (String) -> Unit) {
    OutlinedTextField(value, change, enabled = enabled, label = { Text(stringResource(label)) }, modifier = Modifier.fillMaxWidth(), singleLine = true)
}
@Composable internal fun TunnelCheck(value: Boolean, enabled: Boolean, label: Int, change: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).toggleable(value = value, enabled = enabled, role = Role.Checkbox, onValueChange = change), verticalAlignment = Alignment.CenterVertically) {
        Checkbox(value, onCheckedChange = null, enabled = enabled)
        Text(stringResource(label), Modifier.padding(start = Spacing.sm))
    }
}
