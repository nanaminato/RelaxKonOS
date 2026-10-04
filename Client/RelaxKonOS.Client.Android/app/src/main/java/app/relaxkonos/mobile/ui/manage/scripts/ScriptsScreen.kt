package app.relaxkonos.mobile.ui.manage.scripts

import app.relaxkonos.mobile.ui.common.PageActionRow
import app.relaxkonos.mobile.ui.common.ActionLabel
import app.relaxkonos.mobile.ui.common.ExecutionStatusChip
import app.relaxkonos.mobile.ui.common.ActivityIndicator
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.GuardianApproval
import app.relaxkonos.mobile.core.net.ScriptRequest
import app.relaxkonos.mobile.core.net.ScriptTask
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.RemotePathField
import app.relaxkonos.mobile.ui.common.RemotePathKind
import app.relaxkonos.mobile.ui.theme.Spacing
import java.util.UUID
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class ScriptsUiState(val loading: Boolean = false, val tasks: List<ScriptTask> = emptyList(),
    val selected: ScriptTask? = null, val error: Boolean = false, val problemCode: String? = null)

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

class ScriptsViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    private val mutable = MutableStateFlow(ScriptsUiState())
    val state = mutable.asStateFlow()
    private var owner: SessionState.Active? = null

    fun load(active: SessionState.Active) {
        if (owner !== active) { owner = active; mutable.value = ScriptsUiState() }
        if (mutable.value.loading) return
        mutable.update { it.copy(loading = true, error = false, problemCode = null) }
        viewModelScope.launch {
            val result = container.scriptTasks.tasks(active)
            if (owner !== active) return@launch
            mutable.update { old -> when (result) {
                is ApiResult.Success -> old.copy(loading = false, tasks = result.value.tasks,
                    error = !result.value.success, problemCode = result.value.problemCode.takeIf(String::isNotBlank))
                is ApiResult.Problem -> old.copy(loading = false, error = true, problemCode = result.code)
                is ApiResult.Transport -> old.copy(loading = false, error = true, problemCode = null)
            } }
        }
    }

    fun select(id: String) {
        val active = owner ?: return
        viewModelScope.launch {
            val result = container.scriptTasks.task(active, id)
            if (owner !== active) return@launch
            when (result) {
                is ApiResult.Success -> mutable.update { it.copy(selected = result.value.task,
                    error = !result.value.success, problemCode = result.value.problemCode.takeIf(String::isNotBlank)) }
                else -> mutable.update { it.copy(error = true) }
            }
        }
    }

    fun submit(request: ScriptRequest) {
        val active = owner ?: run { request.approval?.password?.fill('\u0000'); return }
        mutable.update { it.copy(loading = true, error = false) }
        val key = UUID.randomUUID().toString()
        viewModelScope.launch {
            val result = try { container.scriptTasks.submit(active, request, key) }
                finally { request.approval?.password?.fill('\u0000') }
            if (owner !== active) return@launch
            when (result) {
                is ApiResult.Success -> {
                    mutable.update { it.copy(loading = false, selected = result.value.task,
                        error = !result.value.success, problemCode = result.value.problemCode.takeIf(String::isNotBlank)) }
                    load(active)
                }
                else -> mutable.update { it.copy(loading = false, error = true) }
            }
        }
    }

    fun cancel(id: String) {
        val active = owner ?: return
        viewModelScope.launch {
            val result = container.scriptTasks.cancel(active, id)
            if (owner !== active) return@launch
            if (result is ApiResult.Success && result.value.success) select(id)
            else mutable.update { it.copy(error = true) }
        }
    }
}

@Composable
fun ScriptsScreen(owner: SessionState.Active, onBack: () -> Unit, modifier: Modifier = Modifier,
    initialTaskId: String? = null) {
    val model: ScriptsViewModel = viewModel()
    val state by model.state.collectAsStateWithLifecycle()
    var editing by remember { mutableStateOf(false) }
    LaunchedEffect(owner, initialTaskId) {
        model.load(owner)
        if (initialTaskId != null) model.select(initialTaskId)
    }
    LaunchedEffect(state.selected?.id) {
        val id = state.selected?.id ?: return@LaunchedEffect
        while (true) {
            delay(2000)
            val selected = model.state.value.selected ?: break
            if (selected.id != id || selected.state !in setOf("queued", "running", "cancelling")) break
            model.select(id)
        }
    }
    if (editing) {
        ScriptEditor(owner, onBack = { editing = false }, onSubmit = { model.submit(it); editing = false }, modifier = modifier)
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
private fun ScriptEditor(owner: SessionState.Active, onBack: () -> Unit, onSubmit: (ScriptRequest) -> Unit,
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
    val environmentLines = environment.lines().filter(String::isNotBlank)
    val validEnvironment = environmentLines.all { line ->
        val key = line.substringBefore('=')
        '=' in line && key.matches(Regex("[A-Za-z_][A-Za-z0-9_]*"))
    }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        ScreenHeader(title = stringResource(R.string.scripts_new), onBack = onBack)
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
