package app.relaxkonos.mobile.ui.manage.scripts

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import app.relaxkonos.mobile.ui.common.PageActionRow
import app.relaxkonos.mobile.ui.common.ActionLabel
import app.relaxkonos.mobile.ui.common.ExecutionStatusChip
import app.relaxkonos.mobile.ui.common.ActivityIndicator
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.activity.compose.BackHandler
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.TextButton
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedCard
import app.relaxkonos.mobile.ui.common.ManagementCard
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.GuardianApproval
import app.relaxkonos.mobile.core.net.ScriptRequest
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.RemotePathField
import app.relaxkonos.mobile.ui.common.RemotePathKind
import app.relaxkonos.mobile.ui.theme.Spacing

private fun scriptStateLabel(state: String): Int = when (state) {
    "queued" -> R.string.scripts_queued
    "running" -> R.string.scripts_running
    "cancelling" -> R.string.scripts_cancelling
    "succeeded" -> R.string.scripts_succeeded
    "failed" -> R.string.scripts_failed_state
    "cancelled" -> R.string.scripts_cancelled
    "timedOut" -> R.string.scripts_timed_out
    "interrupted" -> R.string.scripts_interrupted
    else -> R.string.scripts_unknown
}

private fun scriptProblemLabel(code: String?): Int = when (code) {
    "scripts.storage_failed" -> R.string.scripts_storage_failed
    "scripts.write_unknown" -> R.string.scripts_write_unknown
    "guardian.agent_permission_denied" -> R.string.guardian_agent_permission
    "guardian.agent_unavailable", "guardian.agent_timeout", "guardian.agent_not_configured" -> R.string.guardian_agent_failed
    "guardian.script_timeout" -> R.string.scripts_timeout_reason
    "guardian.script_agent_restarted" -> R.string.scripts_agent_restart_reason
    "guardian.script_launch_failed", "guardian.run_as_launch_failed", "guardian.run_as_platform_not_supported" -> R.string.scripts_launch_reason
    else -> R.string.scripts_failed
}
@Composable
fun ScriptsScreen(owner: SessionState.Active, onBack: () -> Unit, modifier: Modifier = Modifier,
    initialTaskId: String? = null) {
    val container = appContainer()
    val model: ScriptsViewModel = viewModel(factory = viewModelFactory {
        initializer { ScriptsViewModel(container.session, container.scriptTasks, container.scriptRequests) }
    })
    val state by model.state.collectAsStateWithLifecycle()
    val editing = state.draft != null
    LaunchedEffect(owner, initialTaskId) {
        model.load(owner)
        if (initialTaskId != null) model.select(initialTaskId)
    }
    LaunchedEffect(owner, editing, state.selected?.id, state.selected?.state, state.error) {
        if (!editing) model.observeSelected()
    }
    if (editing) {
        androidx.compose.runtime.key(owner) {
            ScriptEditor(owner, state, requireNotNull(state.draft), model::updateDraft,
                onBack = model::closeEditor, onSubmit = model::submit, onVerify = model::verifyRequest, modifier = modifier)
        }
        return
    }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(title = stringResource(R.string.scripts_title), onBack = onBack)
        Text(stringResource(R.string.scripts_identity, owner.userName))
        state.pending?.let { Text(stringResource(R.string.scripts_pending_task, it.taskId)) }
        if (state.loading) ActivityIndicator(stringResource(R.string.scripts_loading))
        OperationMessageDialog(if (state.error && !state.loading) stringResource(scriptProblemLabel(state.problemCode)) else null)
        PageActionRow(refresh = {
            OutlinedButton(onClick = { model.load(owner) }, enabled = !state.loading) { ActionLabel(R.string.common_refresh) }
        }, actions = {
            Button(onClick = model::openEditor,
                enabled = !state.loading && state.pending == null && state.journalAvailable) { Text(stringResource(R.string.scripts_new)) }
        })
        state.tasks.forEach { task ->
            OutlinedCard(onClick = { model.select(task.id) }, modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.fillMaxWidth().padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text(task.executablePath)
                    Text(task.createdAt, style = MaterialTheme.typography.bodySmall)
                    ExecutionStatusChip(stringResource(scriptStateLabel(task.state)), task.state)
                }
            }
        }
        state.selected?.let { task ->
            ManagementCard {
            Text(task.executablePath, style = MaterialTheme.typography.titleMedium)
            ExecutionStatusChip(stringResource(scriptStateLabel(task.state)), task.state)
            Text("${task.runAs} · ${task.exitCode?.toString() ?: "—"}")
            task.problemCode?.let { Text(stringResource(scriptProblemLabel(it))) }
            if (task.state in setOf("queued", "running")) OutlinedButton(onClick = { model.cancel(task.id) }, enabled = !state.loading && state.pending == null && state.journalAvailable) {
                Text(stringResource(R.string.scripts_cancel))
            }
            if (task.outputTruncated) Text(stringResource(R.string.scripts_truncated))
            SelectionContainer { Column { task.output.forEach { line ->
                Text("${line.timestamp} [${line.stream}] ${line.text}", style = MaterialTheme.typography.bodySmall)
            } } }
            }
        }
    }
}

@Composable
internal fun ScriptEditor(owner: SessionState.Active, state: ScriptsUiState, draft: ScriptDraft,
    onDraftChange: (ScriptDraft) -> Unit, onBack: () -> Unit, onSubmit: (ScriptRequest) -> Unit, onVerify: () -> Unit,
    modifier: Modifier = Modifier) {
    val executable = draft.executable
    val arguments = draft.arguments
    val directory = draft.directory
    val environment = draft.environment
    val timeout = draft.timeout
    val runAs = draft.runAs
    val adminName = draft.adminName
    var adminPassword by remember { mutableStateOf("") }
    var confirmLeave by remember { mutableStateOf(false) }
    val dirty = draft.dirty(owner.userName) || adminPassword.isNotEmpty()
    val leave = { if (!state.loading) { if (dirty) confirmLeave = true else onBack() } }
    val editable = !state.loading && state.pending == null && state.journalAvailable && state.problemCode != "scripts.write_unknown"
    BackHandler(onBack = leave)
    if (confirmLeave) AlertDialog(
        onDismissRequest = { confirmLeave = false },
        title = { Text(stringResource(R.string.ui_discard_draft_title)) },
        text = { Text(stringResource(R.string.ui_discard_draft_message)) },
        confirmButton = { TextButton(onClick = { adminPassword = ""; onBack() }) {
            Text(stringResource(R.string.editor_discard_changes))
        } },
        dismissButton = { TextButton(onClick = { confirmLeave = false }) {
            Text(stringResource(R.string.common_cancel))
        } },
    )
    val environmentValues = scriptEnvironment(environment)
    Column(modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        ScreenHeader(title = stringResource(R.string.scripts_new), onBack = leave)
        if (state.loading) ActivityIndicator(stringResource(R.string.scripts_loading))
        if (state.error && !state.loading) Text(stringResource(scriptProblemLabel(state.problemCode)),
            color = MaterialTheme.colorScheme.error)
        state.pending?.let { pending ->
            Text(stringResource(R.string.scripts_pending_task, pending.taskId))
        }
        if (state.pending != null || !state.journalAvailable)
            OutlinedButton(onClick = onVerify, enabled = !state.loading) { Text(stringResource(R.string.scripts_verify)) }
        RemotePathField(executable, { onDraftChange(draft.copy(executable = it)) }, R.string.guardian_executable,
            RemotePathKind.File, modifier = Modifier.fillMaxWidth(), enabled = editable)
        Text(stringResource(R.string.guardian_arguments))
        arguments.forEachIndexed { index, value ->
            Row(Modifier.fillMaxWidth()) {
                OutlinedTextField(value, { next -> onDraftChange(draft.copy(arguments = arguments.mapIndexed { i, old -> if (i == index) next else old })) },
                    enabled = editable, isError = value.length > 4096 || '\u0000' in value,
                    label = { Text(stringResource(R.string.guardian_argument_number, index + 1)) },
                    modifier = Modifier.weight(1f), maxLines = 4)
                IconButton(onClick = { onDraftChange(draft.copy(arguments = arguments.filterIndexed { i, _ -> i != index })) },
                    enabled = editable, modifier = Modifier.size(48.dp)) {
                    Icon(Icons.Default.Close, stringResource(R.string.guardian_remove_argument, index + 1))
                }
            }
        }
        if (!draft.validArguments) Text(stringResource(R.string.scripts_arguments_invalid), color = MaterialTheme.colorScheme.error)
        OutlinedButton(onClick = { onDraftChange(draft.copy(arguments = arguments + "")) },
            enabled = editable && arguments.size < 64) { Text(stringResource(R.string.guardian_add_argument)) }
        RemotePathField(directory, { onDraftChange(draft.copy(directory = it)) }, R.string.guardian_directory,
            RemotePathKind.Directory, modifier = Modifier.fillMaxWidth(), enabled = editable)
        OutlinedTextField(environment, { onDraftChange(draft.copy(environment = it)) }, enabled = editable,
            isError = environmentValues == null,
            supportingText = { if (environmentValues == null) Text(stringResource(R.string.scripts_environment_invalid)) },
            label = { Text(stringResource(R.string.scripts_environment)) }, modifier = Modifier.fillMaxWidth(), minLines = 3)
        OutlinedTextField(timeout, { onDraftChange(draft.copy(timeout = it.filter(Char::isDigit))) }, enabled = editable, label = { Text(stringResource(R.string.scripts_timeout)) })
        OutlinedTextField(runAs, { onDraftChange(draft.copy(runAs = it)) }, enabled = editable, label = { Text(stringResource(R.string.guardian_run_as)) })
        if (runAs.trim() != owner.userName) {
            Text(stringResource(R.string.guardian_approval_notice))
            OutlinedTextField(adminName, { onDraftChange(draft.copy(adminName = it)) }, enabled = editable, label = { Text(stringResource(R.string.guardian_admin_name)) })
            OutlinedTextField(adminPassword, { adminPassword = it }, enabled = editable, label = { Text(stringResource(R.string.guardian_admin_password)) },
                visualTransformation = PasswordVisualTransformation())
        }
        Text(stringResource(R.string.scripts_durability))
        Button(onClick = {
            val approval = if (runAs.trim() == owner.userName) null else GuardianApproval(adminName, adminPassword.toCharArray())
            onSubmit(ScriptRequest(executable.trim(), arguments, directory.trim(),
                requireNotNull(environmentValues),
                timeout.toInt(), runAs.trim(), approval))
            adminPassword = ""
        }, enabled = editable && draft.validArguments && executable.isNotBlank() && directory.isNotBlank() && timeout.toIntOrNull()?.let { it in 1..3600 } == true &&
            environmentValues != null && (runAs.trim() == owner.userName || adminName.isNotBlank() && adminPassword.isNotBlank())) {
            Text(stringResource(R.string.scripts_run))
        }
    }
}
