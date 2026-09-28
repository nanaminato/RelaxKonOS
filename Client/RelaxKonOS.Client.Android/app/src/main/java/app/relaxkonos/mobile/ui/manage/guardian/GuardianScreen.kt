package app.relaxkonos.mobile.ui.manage.guardian

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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
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
import app.relaxkonos.mobile.core.net.GuardianDefinition
import app.relaxkonos.mobile.core.net.GuardianHealthCheck
import app.relaxkonos.mobile.core.net.GuardianLog
import app.relaxkonos.mobile.core.net.GuardianStatus
import app.relaxkonos.mobile.core.net.GuardianWorkload
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.theme.Spacing
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class GuardianUiState(
    val loading: Boolean = false,
    val status: GuardianStatus? = null,
    val workloads: List<GuardianWorkload> = emptyList(),
    val selectedId: String? = null,
    val definition: GuardianDefinition? = null,
    val logs: List<GuardianLog> = emptyList(),
    val error: Boolean = false,
    val problemCode: String? = null,
)

private fun guardianProblemLabel(code: String?): Int = when (code) {
    "guardian.run_as_identity_mismatch" -> R.string.guardian_identity_changed
    "guardian.run_as_launch_failed", "guardian.run_as_permission_denied", "guardian.run_as_platform_not_supported" -> R.string.guardian_launch_failed
    "guardian.validation_executable", "guardian.validation_working_directory", "guardian.validation_failed" -> R.string.guardian_configuration_invalid
    "guardian.agent_unavailable", "guardian.agent_timeout", "guardian.agent_not_configured" -> R.string.guardian_agent_failed
    else -> R.string.guardian_failed
}

class GuardianViewModel(application: Application) : AndroidViewModel(application) {
    private val session = getApplication<RelaxKonApplication>().container.session
    private val gateway = getApplication<RelaxKonApplication>().container.gateway
    private val mutable = MutableStateFlow(GuardianUiState())
    val state = mutable.asStateFlow()
    private var owner: SessionState.Active? = null

    fun load(active: SessionState.Active) {
        if (owner !== active) { owner = active; mutable.value = GuardianUiState() }
        if (mutable.value.loading) return
        mutable.update { it.copy(loading = true, error = false, problemCode = null) }
        viewModelScope.launch {
            val status = session.authenticated { url, token -> gateway.guardianStatus(url, token) }
            val workloads = session.authenticated { url, token -> gateway.guardianWorkloads(url, token) }
            if (owner !== active) return@launch
            mutable.update { old -> old.copy(loading = false,
                status = (status as? ApiResult.Success)?.value,
                workloads = (workloads as? ApiResult.Success)?.value ?: old.workloads,
                error = status !is ApiResult.Success || workloads !is ApiResult.Success) }
        }
    }

    fun select(id: String?) {
        mutable.update { it.copy(selectedId = id, definition = null, logs = emptyList(), error = false) }
        if (id == null) return
        val active = owner ?: return
        viewModelScope.launch {
            val definition = session.authenticated { url, token -> gateway.guardianDefinition(url, token, id) }
            val logs = session.authenticated { url, token -> gateway.guardianLogs(url, token, id) }
            if (owner !== active || mutable.value.selectedId != id) return@launch
            mutable.update { it.copy(
                definition = (definition as? ApiResult.Success)?.value,
                logs = (logs as? ApiResult.Success)?.value.orEmpty(),
                error = definition !is ApiResult.Success || logs !is ApiResult.Success,
            ) }
        }
    }

    fun act(id: String, action: String) = mutate(call = { url, token -> gateway.guardianAction(url, token, id, action) })
    fun delete(id: String) = mutate(call = { url, token -> gateway.guardianDelete(url, token, id) })
    fun save(definition: GuardianDefinition, approval: GuardianApproval?) = mutate(
        call = { url, token -> gateway.guardianSave(url, token, definition, approval) },
        after = { approval?.password?.fill('\u0000') },
    )

    private fun mutate(call: suspend (String, String) -> ApiResult<app.relaxkonos.mobile.core.net.GuardianOperation>, after: () -> Unit = {}) {
        val active = owner ?: run { after(); return }
        mutable.update { it.copy(loading = true, error = false, problemCode = null) }
        viewModelScope.launch {
            val result = try { session.authenticated(call) } finally { after() }
            if (owner !== active) return@launch
            when (result) {
                is ApiResult.Success -> {
                    if (result.value.success) {
                        mutable.update { it.copy(loading = false, selectedId = null, definition = null, logs = emptyList()) }
                        load(active)
                    } else mutable.update { it.copy(loading = false, error = true, problemCode = result.value.problemCode) }
                }
                else -> mutable.update { it.copy(loading = false, error = true) }
            }
        }
    }
}

@Composable
fun GuardianScreen(owner: SessionState.Active, onBack: () -> Unit, modifier: Modifier = Modifier) {
    val model: GuardianViewModel = viewModel()
    val state by model.state.collectAsState()
    var editing by remember { mutableStateOf<GuardianDefinition?>(null) }
    var deleteTarget by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(owner) { model.load(owner) }
    if (deleteTarget != null) AlertDialog(onDismissRequest = { deleteTarget = null },
        title = { Text(stringResource(R.string.guardian_delete_title)) },
        text = { Text(stringResource(R.string.guardian_delete_message)) },
        confirmButton = { TextButton(onClick = { deleteTarget?.let(model::delete); deleteTarget = null }) { Text(stringResource(R.string.common_delete)) } },
        dismissButton = { TextButton(onClick = { deleteTarget = null }) { Text(stringResource(R.string.common_cancel)) } })
    if (editing != null) {
        GuardianEditor(owner, editing!!, onCancel = { editing = null }, onSave = { definition, approval ->
            model.save(definition, approval)
            editing = null
        }, modifier = modifier)
        return
    }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(title = stringResource(R.string.guardian_title), onBack = onBack)
        if (state.loading) Text(stringResource(R.string.guardian_loading))
        state.status?.let { status -> Text(stringResource(if (status.running) R.string.guardian_running else R.string.guardian_unavailable)) }
        if (state.error) Text(stringResource(guardianProblemLabel(state.problemCode)), color = MaterialTheme.colorScheme.error)
        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedButton(onClick = { model.load(owner) }, enabled = !state.loading) { Text(stringResource(R.string.common_refresh)) }
            Button(onClick = { editing = GuardianDefinition(UUID.randomUUID().toString(), "", "", emptyList(), "", false, 15, 3, null, owner.userName) },
                enabled = state.status?.running == true) { Text(stringResource(R.string.guardian_create)) }
        }
        state.workloads.forEach { workload ->
            OutlinedButton(onClick = { model.select(workload.id) }, modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.fillMaxWidth()) {
                    Text(workload.name)
                    Text("${workload.desiredState} · ${workload.actualState} · ${workload.healthStatus.orEmpty()}", style = MaterialTheme.typography.bodySmall)
                }
            }
        }
        state.selectedId?.let { id ->
            val workload = state.workloads.firstOrNull { it.id == id }
            Text(workload?.name.orEmpty(), style = MaterialTheme.typography.titleLarge)
            workload?.let {
                Text(stringResource(R.string.guardian_detail, it.restartCount, it.healthFailureCount, it.processId?.toString() ?: "—"))
                Text("${it.desiredState} · ${it.actualState} · ${it.healthStatus ?: "—"}")
                it.lastExitCode?.let { code -> Text(stringResource(R.string.terminal_exit_code, code)) }
                if (it.lastProblemCode != null) Text(stringResource(guardianProblemLabel(it.lastProblemCode)))
                Text("${it.runAs ?: owner.userName} · ${it.executablePath.orEmpty()}")
            }
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                listOf("start" to R.string.guardian_start, "stop" to R.string.guardian_stop,
                    "restart" to R.string.guardian_restart).forEach { (action, label) ->
                    OutlinedButton(onClick = { model.act(id, action) }, enabled = !state.loading) { Text(stringResource(label)) }
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                OutlinedButton(onClick = { state.definition?.let { editing = it } }, enabled = state.definition != null) { Text(stringResource(R.string.guardian_edit)) }
                OutlinedButton(onClick = { deleteTarget = id }, enabled = !state.loading) { Text(stringResource(R.string.common_delete)) }
                OutlinedButton(onClick = { model.select(id) }) { Text(stringResource(R.string.guardian_logs)) }
            }
            SelectionContainer { Column { state.logs.takeLast(200).forEach { Text("${it.timestamp} [${it.stream}] ${it.message}", style = MaterialTheme.typography.bodySmall) } } }
        }
    }
}

@Composable
private fun GuardianEditor(owner: SessionState.Active, initial: GuardianDefinition,
    onCancel: () -> Unit, onSave: (GuardianDefinition, GuardianApproval?) -> Unit, modifier: Modifier = Modifier) {
    var name by remember(initial.id) { mutableStateOf(initial.name) }
    var executable by remember(initial.id) { mutableStateOf(initial.executablePath) }
    var arguments by remember(initial.id) { mutableStateOf(initial.arguments.joinToString("\n")) }
    var directory by remember(initial.id) { mutableStateOf(initial.workingDirectory) }
    var runAs by remember(initial.id) { mutableStateOf(initial.runAs ?: owner.userName) }
    var enabled by remember(initial.id) { mutableStateOf(initial.enabledOnBoot) }
    var attempts by remember(initial.id) { mutableStateOf(initial.maxRestartAttempts.toString()) }
    var healthType by remember(initial.id) { mutableStateOf(initial.healthCheck?.type ?: "") }
    var healthTarget by remember(initial.id) { mutableStateOf(initial.healthCheck?.target ?: "") }
    var adminName by remember(initial.id) {
        mutableStateOf(if (owner.serverPlatform.contains("windows", ignoreCase = true)) "Administrator" else "root")
    }
    var adminPassword by remember(initial.id) { mutableStateOf("") }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        ScreenHeader(title = stringResource(R.string.guardian_editor), onBack = onCancel)
        OutlinedTextField(name, { name = it }, label = { Text(stringResource(R.string.guardian_name)) }, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(executable, { executable = it }, label = { Text(stringResource(R.string.guardian_executable)) }, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(arguments, { arguments = it }, label = { Text(stringResource(R.string.guardian_arguments)) }, modifier = Modifier.fillMaxWidth(), minLines = 3)
        OutlinedTextField(directory, { directory = it }, label = { Text(stringResource(R.string.guardian_directory)) }, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(runAs, { runAs = it }, label = { Text(stringResource(R.string.guardian_run_as)) }, modifier = Modifier.fillMaxWidth())
        Row { Checkbox(enabled, { enabled = it }); Text(stringResource(R.string.guardian_boot), modifier = Modifier.padding(top = Spacing.sm)) }
        OutlinedTextField(attempts, { attempts = it.filter(Char::isDigit) }, label = { Text(stringResource(R.string.guardian_restart_attempts)) })
        OutlinedTextField(healthType, { healthType = it }, label = { Text(stringResource(R.string.guardian_health_type)) })
        OutlinedTextField(healthTarget, { healthTarget = it }, label = { Text(stringResource(R.string.guardian_health_target)) })
        if (runAs.trim() != owner.userName) {
            Text(stringResource(R.string.guardian_approval_notice))
            OutlinedTextField(adminName, { adminName = it }, label = { Text(stringResource(R.string.guardian_admin_name)) })
            OutlinedTextField(adminPassword, { adminPassword = it }, label = { Text(stringResource(R.string.guardian_admin_password)) },
                visualTransformation = PasswordVisualTransformation())
        }
        Button(onClick = {
            val approval = if (runAs.trim() != owner.userName) GuardianApproval(adminName, adminPassword.toCharArray()) else null
            onSave(initial.copy(name = name.trim(), executablePath = executable.trim(),
                arguments = arguments.lines().filter(String::isNotBlank), workingDirectory = directory.trim(),
                runAs = runAs.trim(), enabledOnBoot = enabled, maxRestartAttempts = attempts.toIntOrNull() ?: 3,
                healthCheck = if (healthType.isBlank()) null else GuardianHealthCheck(healthType.trim(), healthTarget.trim().ifBlank { null }, 15, 5, 3)), approval)
            adminPassword = ""
        }, enabled = name.isNotBlank() && executable.isNotBlank() && directory.isNotBlank() &&
            (runAs.trim() == owner.userName || (adminName.isNotBlank() && adminPassword.isNotBlank()))) {
            Text(stringResource(R.string.common_save))
        }
    }
}
