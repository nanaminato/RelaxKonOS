package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

private enum class TunnelEditorDecisionKind { Save, Reload, Discard }
private class TunnelEditorDecision(val snapshot: Any, val kind: TunnelEditorDecisionKind)

@Composable private fun TunnelEditorForm(state: TunnelsState, draft: Any, title: Int, target: String,
    dirty: Boolean, valid: Boolean, saveMessage: Int, isCurrent: () -> Boolean,
    closeEditor: () -> Unit, saveEditor: () -> Unit, reloadEditor: (() -> Unit)?,
    content: @Composable (Boolean, (() -> Unit) -> Unit) -> Unit) {
    var decision by remember { mutableStateOf<TunnelEditorDecision?>(null) }
    var disposed by remember { mutableStateOf(false) }
    val snapshot = remember(draft, state) { Any() }
    val currentSnapshot by rememberUpdatedState(snapshot)
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var interactionEpoch by remember(lifecycle) { mutableIntStateOf(0) }
    val observedEpoch = interactionEpoch
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_PAUSE || event == Lifecycle.Event.ON_RESUME) {
                interactionEpoch++; decision = null
            }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); disposed = true; decision = null }
    }
    LaunchedEffect(snapshot) { if (decision?.snapshot !== snapshot) decision = null }
    fun current() = !disposed && currentSnapshot === snapshot && interactionEpoch == observedEpoch &&
        lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) && !state.busy && isCurrent()
    val locked = state.busy || state.pending.isNotEmpty() || decision != null
    fun change(action: () -> Unit) { if (current() && !locked && decision == null) action() }
    fun finish() { disposed = true; decision = null; closeEditor() }
    fun close() {
        if (current() && decision == null) {
            if (dirty) decision = TunnelEditorDecision(snapshot, TunnelEditorDecisionKind.Discard) else finish()
        }
    }
    fun review(kind: TunnelEditorDecisionKind) {
        if (current() && !locked && decision == null && (kind != TunnelEditorDecisionKind.Save || valid))
            decision = TunnelEditorDecision(snapshot, kind)
    }
    val scroll = rememberScrollState()
    key(observedEpoch, state.editorVersion) {
        AlertDialog(onDismissRequest = ::close, modifier = Modifier.imePadding(), title = { Text(stringResource(title)) },
            text = {
                Column(Modifier.heightIn(max = 420.dp).verticalScroll(scroll), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text(stringResource(R.string.tunnels_saved_note))
                    content(locked, ::change)
                    EditorStatus(state, !valid)
                    if (reloadEditor != null) key(snapshot) {
                        TextButton(enabled = !locked, onClick = { review(TunnelEditorDecisionKind.Reload) }) { Text(stringResource(R.string.tunnels_reload)) }
                    }
                }
            },
            confirmButton = { key(snapshot) {
                Button(enabled = !locked && valid, onClick = { review(TunnelEditorDecisionKind.Save) }) { ActionLabel(R.string.common_save) }
            } },
            dismissButton = { key(snapshot) {
                TextButton(enabled = !state.busy, onClick = ::close) { Text(stringResource(R.string.common_close)) }
            } })
    }
    decision?.takeIf { it.snapshot === snapshot }?.let { review -> key(review) {
        val dismiss = { if (current() && decision === review) decision = null }
        val message = when (review.kind) {
            TunnelEditorDecisionKind.Save -> saveMessage
            TunnelEditorDecisionKind.Reload -> R.string.tunnels_reload_confirm
            TunnelEditorDecisionKind.Discard -> R.string.tunnels_discard_confirm
        }
        AlertDialog(onDismissRequest = dismiss, title = { Text(stringResource(R.string.tunnels_confirm)) },
            text = { Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState())) { Text(target); Text(stringResource(message)) } },
            confirmButton = { Button(onClick = {
                if (current() && decision === review) {
                    decision = null
                    when (review.kind) {
                        TunnelEditorDecisionKind.Save -> if (state.pending.isEmpty() && valid) saveEditor()
                        TunnelEditorDecisionKind.Reload -> if (state.pending.isEmpty()) reloadEditor?.invoke()
                        TunnelEditorDecisionKind.Discard -> finish()
                    }
                }
            }) { Text(stringResource(R.string.tunnels_confirm)) } },
            dismissButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.common_cancel)) } })
    } }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun TunnelProfileEditor(state: TunnelsState,
    update: (TunnelProfileDraft) -> Unit, closeEditor: () -> Unit, saveEditor: () -> Unit,
    reloadEditor: () -> Unit, detect: () -> Unit, isCurrent: () -> Boolean) {
    val draft = state.profileDraft ?: return
    TunnelEditorForm(state, draft, R.string.tunnels_profile_editor, draft.name + " · " + draft.id.orEmpty(),
        draft != state.initialProfile, draft.request() != null, R.string.tunnels_profile_save_confirm,
        isCurrent, closeEditor, saveEditor, reloadEditor.takeIf { draft.id != null }) { locked, change ->
        TunnelCard {
            TunnelText(draft.name, !locked, R.string.tunnels_name) { change { update(draft.copy(name = it)) } }
            TunnelText(draft.host, !locked, R.string.tunnels_host) { change { update(draft.copy(host = it)) } }
            TunnelText(draft.port, !locked, R.string.tunnels_server_port) { change { update(draft.copy(port = it)) } }
        }
        TunnelCard {
            Text(stringResource(R.string.tunnels_security), style = MaterialTheme.typography.titleSmall)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) { TunnelAuth.entries.forEach { auth -> FilterChip(draft.auth == auth, { change { update(draft.copy(auth = auth)) } }, enabled = !locked, label = { Text(tunnelAuthLabel(auth)) }) } }
            Text(stringResource(R.string.tunnels_token_editor_note))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) { TunnelTls.entries.forEach { tls -> FilterChip(draft.tls == tls, { change { update(draft.copy(tls = tls)) } }, enabled = !locked, label = { Text(tunnelTlsLabel(tls)) }) } }
            if (draft.tls == TunnelTls.Disable) Text(stringResource(R.string.tunnels_tls_warning), color = MaterialTheme.colorScheme.error)
        }
        TunnelCard {
            Text(stringResource(R.string.tunnels_runtime_title), style = MaterialTheme.typography.titleSmall)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) { TunnelRuntimeMode.entries.forEach { mode -> FilterChip(draft.mode == mode, { change { update(draft.copy(mode = mode)) } }, enabled = !locked, label = { Text(tunnelModeLabel(mode)) }) } }
            if (draft.mode == TunnelRuntimeMode.External) {
                if (!locked) RemotePathField(draft.path, { change { update(draft.copy(path = it)) } }, R.string.tunnels_external_path, RemotePathKind.File) else Text(draft.path)
                Text(stringResource(R.string.tunnels_external_note))
                key(draft, state) {
                    OutlinedButton(enabled = !locked && TunnelInputs.absolutePath(draft.path.trim()), onClick = { change { if (TunnelInputs.absolutePath(draft.path.trim())) detect() } }) { Text(stringResource(R.string.tunnels_probe)) }
                }
                when (val detected = state.detected) {
                    is ApiResult.Success -> { ExecutionStatusChip(tunnelRuntimeLabel(detected.value.state), detected.value.state.wire, task = false); detected.value.version?.let { Text(stringResource(R.string.tunnels_version_value, it)) }; detected.value.problemCode.takeIf(String::isNotBlank)?.let { Text(tunnelProblemLabel(it)) } }
                    null -> Unit
                    else -> Text(stringResource(R.string.tunnels_unknown))
                }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun TunnelDefinitionEditor(state: TunnelsState, model: TunnelsViewModel) {
    val draft = state.definitionDraft ?: return
    val profiles = (state.facts as? ApiResult.Success)?.value?.profiles.orEmpty()
    TunnelEditorForm(state, draft, R.string.tunnels_definition_editor, draft.name + " · " + draft.id.orEmpty(),
        draft != state.initialDefinition, draft.request() != null && profiles.any { it.id == draft.profileId }, R.string.tunnels_definition_save_confirm,
        { model.definitionIsCurrent(draft) && !model.state.busy }, { model.closeDefinition(draft) }, { model.saveDefinition(draft) },
        { model.reloadDefinition(draft) }.takeIf { draft.id != null }) { locked, change ->
        TunnelCard {
            TunnelText(draft.name, !locked, R.string.tunnels_name) { change { model.updateDefinition(draft, draft.copy(name = it)) } }
            Text(stringResource(R.string.tunnels_profiles))
            profiles.forEach { profile -> TextButton(enabled = !locked, onClick = { change { model.updateDefinition(draft, draft.copy(profileId = profile.id)) } }) { Text((if (draft.profileId == profile.id) "✓ " else "") + profile.name) } }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) { TunnelProtocol.entries.forEach { protocol -> FilterChip(draft.protocol == protocol, { change { model.updateDefinition(draft, draft.copy(protocol = protocol)) } }, enabled = !locked, label = { Text(protocol.wire.uppercase()) }) } }
        }
        TunnelCard {
            Text(stringResource(R.string.tunnels_destination), style = MaterialTheme.typography.titleSmall)
            Text(stringResource(R.string.tunnels_local_help), style = MaterialTheme.typography.bodySmall)
            TunnelText(draft.localHost, !locked, R.string.tunnels_local_host) { change { model.updateDefinition(draft, draft.copy(localHost = it)) } }
            TunnelText(draft.localPort, !locked, R.string.tunnels_local_port) { change { model.updateDefinition(draft, draft.copy(localPort = it)) } }
            if (draft.protocol.usesPort) TunnelText(draft.remotePort, !locked, R.string.tunnels_remote_port) { change { model.updateDefinition(draft, draft.copy(remotePort = it)) } }
            else TunnelText(draft.domain, !locked, R.string.tunnels_domain) { change { model.updateDefinition(draft, draft.copy(domain = it)) } }
        }
        TunnelCard {
            TunnelCheck(draft.enabled, !locked, R.string.tunnels_enabled) { change { model.updateDefinition(draft, draft.copy(enabled = it)) } }
            TunnelCheck(draft.encryption, !locked, R.string.tunnels_encryption) { change { model.updateDefinition(draft, draft.copy(encryption = it)) } }
            TunnelCheck(draft.compression, !locked, R.string.tunnels_compression) { change { model.updateDefinition(draft, draft.copy(compression = it)) } }
        }
    }
}

internal val TunnelsState.editorOpen: Boolean
    get() = profileDraft != null || definitionDraft != null || frpsDraft != null

@Composable internal fun TunnelEditorFeedback(state: TunnelsState) {
    if (!state.busy) {
        state.problemCode?.let { Text(tunnelProblemLabel(it), color = MaterialTheme.colorScheme.error) }
        if (state.uncertain || state.pending.isNotEmpty())
            Text(stringResource(R.string.tunnels_uncertain), color = MaterialTheme.colorScheme.error)
    }
    RefreshProgressIndicator(visible = state.busy)
}

@Composable private fun EditorStatus(state: TunnelsState, invalid: Boolean) {
    if (invalid) Text(stringResource(R.string.tunnels_validation), color = MaterialTheme.colorScheme.error)
    TunnelEditorFeedback(state)
}
@Composable internal fun TunnelText(value: String, enabled: Boolean, label: Int, change: (String) -> Unit) {
    OutlinedTextField(value, change, enabled = enabled, label = { Text(stringResource(label)) }, modifier = Modifier.fillMaxWidth(), singleLine = true)
}
@Composable internal fun TunnelCheck(value: Boolean, enabled: Boolean, label: Int, change: (Boolean) -> Unit) {
    CheckboxOption(value, stringResource(label), enabled, change)
}