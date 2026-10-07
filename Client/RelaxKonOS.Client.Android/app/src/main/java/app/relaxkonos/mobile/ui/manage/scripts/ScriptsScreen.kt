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
    val model: ScriptsViewModel = viewModel()
    val state by model.state.collectAsStateWithLifecycle()
    var editing by remember(owner) { mutableStateOf(false) }
    LaunchedEffect(owner, initialTaskId) {
        model.load(owner)
        if (initialTaskId != null) model.select(initialTaskId)
    }
    LaunchedEffect(owner, state.selected?.id) {
        model.observeSelected()
    }
    if (editing) {
        androidx.compose.runtime.key(owner) {
            ScriptEditor(owner, onBack = { editing = false }, onSubmit = { model.submit(it); editing = false }, modifier = modifier)
        }
        return
    }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(title = stringResource(R.string.scripts_title), onBack = onBack)
        Text(stringResource(R.string.scripts_identity, owner.userName))
        if (state.loading) ActivityIndicator(stringResource(R.string.scripts_loading))
        OperationMessageDialog(if (state.error && !state.loading) stringResource(R.string.scripts_failed) else null)
        PageActionRow(refresh = {
            OutlinedButton(onClick = { model.load(owner) }, enabled = !state.loading) { ActionLabel(R.string.common_refresh) }
        }, actions = {
            Button(onClick = { editing = true }, enabled = !state.loading) { Text(stringResource(R.string.scripts_new)) }
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
            if (task.state in setOf("queued", "running")) OutlinedButton(onClick = { model.cancel(task.id) }, enabled = !state.loading) {
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
internal fun ScriptEditor(owner: SessionState.Active, onBack: () -> Unit, onSubmit: (ScriptRequest) -> Unit,
    modifier: Modifier = Modifier) {
    var executable by remember { mutableStateOf("") }
    var arguments by remember { mutableStateOf("") }
    var directory by remember { mutableStateOf("") }
    var environment by remember { mutableStateOf("") }
    var timeout by remember { mutableStateOf("300") }
    var runAs by remember { mutableStateOf(owner.userName) }
    var adminName by remember(owner) {
        mutableStateOf("")
    }
    var adminPassword by remember { mutableStateOf("") }
    var confirmLeave by remember { mutableStateOf(false) }
    val dirty = executable.isNotEmpty() || arguments.isNotEmpty() || directory.isNotEmpty() ||
        environment.isNotEmpty() || timeout != "300" || runAs != owner.userName ||
        adminName.isNotEmpty() || adminPassword.isNotEmpty()
    val leave = { if (dirty) confirmLeave = true else onBack() }
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
    val environmentLines = environment.lines().filter(String::isNotBlank)
    val validEnvironment = environmentLines.all { line ->
        val key = line.substringBefore('=')
        '=' in line && key.matches(Regex("[A-Za-z_][A-Za-z0-9_]*"))
    }
    Column(modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        ScreenHeader(title = stringResource(R.string.scripts_new), onBack = leave)
        RemotePathField(executable, { executable = it }, R.string.guardian_executable,
            RemotePathKind.File, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(arguments, { arguments = it }, label = { Text(stringResource(R.string.guardian_arguments)) }, modifier = Modifier.fillMaxWidth(), minLines = 3)
        RemotePathField(directory, { directory = it }, R.string.guardian_directory,
            RemotePathKind.Directory, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(environment, { environment = it }, label = { Text(stringResource(R.string.scripts_environment)) }, modifier = Modifier.fillMaxWidth(), minLines = 3)
        OutlinedTextField(timeout, { timeout = it.filter(Char::isDigit) }, label = { Text(stringResource(R.string.scripts_timeout)) })
        OutlinedTextField(runAs, { runAs = it }, label = { Text(stringResource(R.string.guardian_run_as)) })
        if (runAs.trim() != owner.userName) {
            Text(stringResource(R.string.guardian_approval_notice))
            OutlinedTextField(adminName, { adminName = it }, label = { Text(stringResource(R.string.guardian_admin_name)) })
            OutlinedTextField(adminPassword, { adminPassword = it }, label = { Text(stringResource(R.string.guardian_admin_password)) },
                visualTransformation = PasswordVisualTransformation())
        }
        Text(stringResource(R.string.scripts_durability))
        Button(onClick = {
            val approval = if (runAs.trim() == owner.userName) null else GuardianApproval(adminName, adminPassword.toCharArray())
            onSubmit(ScriptRequest(executable.trim(), arguments.lines().filter(String::isNotBlank), directory.trim(),
                environmentLines.associate { it.substringBefore('=') to it.substringAfter('=') },
                timeout.toInt(), runAs.trim(), approval))
            adminPassword = ""
        }, enabled = executable.isNotBlank() && directory.isNotBlank() && timeout.toIntOrNull()?.let { it in 1..3600 } == true &&
            validEnvironment && (runAs.trim() == owner.userName || adminName.isNotBlank() && adminPassword.isNotBlank())) {
            Text(stringResource(R.string.scripts_run))
        }
    }
}
