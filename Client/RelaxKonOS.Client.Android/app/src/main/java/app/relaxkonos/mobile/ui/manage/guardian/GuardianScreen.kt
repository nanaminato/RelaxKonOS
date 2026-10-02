package app.relaxkonos.mobile.ui.manage.guardian

import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import app.relaxkonos.mobile.ui.common.StatusTone
import android.app.Application
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.runtime.DisposableEffect
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.horizontalScroll
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
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
import app.relaxkonos.mobile.ui.common.RemotePathField
import app.relaxkonos.mobile.ui.common.RemotePathKind
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
    val unknown: Boolean = false,
    val stale: Boolean = true,
)

private fun guardianProblemLabel(code: String?): Int = when (code) {
    "guardian.agent_permission_denied" -> R.string.guardian_agent_permission
    "guardian.run_as_identity_mismatch" -> R.string.guardian_identity_changed
    "guardian.run_as_launch_failed", "guardian.run_as_permission_denied", "guardian.run_as_platform_not_supported" -> R.string.guardian_launch_failed
    "guardian.definition_changed" -> R.string.guardian_definition_changed
    "guardian.validation_executable", "guardian.validation_working_directory", "guardian.validation_failed" -> R.string.guardian_configuration_invalid
    "guardian.agent_unavailable", "guardian.agent_timeout", "guardian.agent_not_configured" -> R.string.guardian_agent_failed
    else -> R.string.guardian_failed
}

class GuardianViewModel(application: Application) : AndroidViewModel(application) {
    private val session = getApplication<RelaxKonApplication>().container.session
    private val repository = getApplication<RelaxKonApplication>().container.guardian
    private val mutable = MutableStateFlow(GuardianUiState())
    val state = mutable.asStateFlow()
    var editing by mutableStateOf<GuardianDraft?>(null)
        private set
    private var owner: SessionState.Active? = null
    private var work: Job? = null
    private var reading: Job? = null
    private var selection: Job? = null
    private var observation: Job? = null
    internal val logObserver = GuardianLogObserver(session, viewModelScope)
    init { viewModelScope.launch { session.state.collect { value ->
        val active = value as? SessionState.Active
        if (owner !== active) { work?.cancel(); reading?.cancel(); selection?.cancel(); observation?.cancel(); logObserver.stop(); owner = active; editing = null; mutable.value = GuardianUiState() }
    } } }
    private fun current(active: SessionState.Active) = owner === active && session.state.value === active
    fun observe(active: SessionState.Active, visible: Boolean) {
        if (visible && session.state.value === active && owner !== active) load(active)
        observation?.cancel(); observation = null
        if (!visible || editing != null || !current(active)) { stopObserving(); return }
        val id = mutable.value.selectedId
        if (id != null && mutable.value.definition == null && selection?.isActive != true) select(id)
        logObserver.observe(active, id)
        observation = viewModelScope.launch {
            while (current(active)) {
                load(active, reconcile = false)
                delay(3_000)
            }
        }
    }
    fun stopObserving() { observation?.cancel(); observation = null; reading?.cancel(); selection?.cancel(); logObserver.stop() }
    fun load(active: SessionState.Active, reconcile: Boolean = true) {
        if (session.state.value !== active) return
        if (owner !== active) { work?.cancel(); reading?.cancel(); selection?.cancel(); observation?.cancel(); logObserver.stop(); owner = active; editing = null; mutable.value = GuardianUiState() }
        if (mutable.value.loading) return
        mutable.update { it.copy(loading = true, error = false, problemCode = null) }
        reading = viewModelScope.launch {
            try {
                val status = repository.status(active)
                val workloads = repository.workloads(active)
                if (!current(active)) return@launch
                mutable.update { old -> old.copy(status = (status as? ApiResult.Success)?.value,
                    workloads = (workloads as? ApiResult.Success)?.value ?: old.workloads,
                    unknown = if (reconcile && status is ApiResult.Success && workloads is ApiResult.Success) false else old.unknown,
                    stale = status !is ApiResult.Success || workloads !is ApiResult.Success || status.value.running != true,
                    error = status !is ApiResult.Success || workloads !is ApiResult.Success,
                    problemCode = (workloads as? ApiResult.Problem)?.code ?: (status as? ApiResult.Problem)?.code
                        ?: (status as? ApiResult.Success)?.value?.problemCode?.takeIf(String::isNotBlank)) }
                if (workloads is ApiResult.Success && mutable.value.selectedId != null && workloads.value.none { it.id == mutable.value.selectedId }) select(null)
            } finally { if (current(active)) mutable.update { it.copy(loading = false) } }
        }
    }
    fun select(id: String?) {
        selection?.cancel()
        mutable.update { it.copy(selectedId = id, definition = null, logs = emptyList(), error = false) }
        val active = owner ?: return
        if (id == null || !current(active)) return
        selection = viewModelScope.launch {
            val definition = repository.definition(active, id)
            val logs = repository.logs(active, id)
            if (!current(active) || mutable.value.selectedId != id) return@launch
            mutable.update { it.copy(definition = (definition as? ApiResult.Success)?.value, logs = (logs as? ApiResult.Success)?.value.orEmpty(),
                error = definition !is ApiResult.Success || logs !is ApiResult.Success, problemCode = (definition as? ApiResult.Problem)?.code) }
        }
    }
    fun beginEdit(value: GuardianDefinition, creating: Boolean) { if (!mutable.value.loading && !mutable.value.unknown && !mutable.value.stale) editing = GuardianDraft(value, creating) }
    fun cancelEdit() { if (!mutable.value.loading) editing = null }
    fun readEditor() {
        val draft = editing ?: return; val active = owner ?: return
        if (!current(active) || mutable.value.loading) return
        mutable.update { it.copy(loading = true, error = false) }
        work = viewModelScope.launch {
            try {
                val result = repository.definition(active, draft.initial.id)
                if (!current(active) || editing !== draft) return@launch
                if (result is ApiResult.Success) { editing = GuardianDraft(result.value, false); mutable.update { it.copy(unknown = false, stale = false, error = false, definition = result.value) } }
                else if (result is ApiResult.Problem && result.status == 404 && draft.creating) mutable.update { it.copy(unknown = false, stale = false, error = false) }
                else mutable.update { it.copy(error = true, problemCode = (result as? ApiResult.Problem)?.code) }
            } finally { if (current(active)) mutable.update { it.copy(loading = false) } }
        }
    }
    fun save(draft: GuardianDraft, definition: GuardianDefinition, approval: GuardianApproval?) {
        val active = owner
        if (active == null || !current(active) || mutable.value.loading || mutable.value.unknown || editing !== draft) { approval?.password?.fill('\u0000'); return }
        mutable.update { it.copy(loading = true, error = false, problemCode = null) }
        work = viewModelScope.launch {
            try {
                val result = repository.save(active, draft.initial.takeUnless { draft.creating }, definition, approval)
                if (!current(active)) return@launch
                if (result is ApiResult.Success) {
                    editing = null; mutable.update { it.copy(definition = result.value, selectedId = result.value.id) }
                } else failure(result)
            } finally {
                approval?.password?.fill('\u0000')
                if (current(active)) { mutable.update { it.copy(loading = false) }; if (editing == null) load(active) }
            }
        }.also { job -> job.invokeOnCompletion { approval?.password?.fill('\u0000') } }
    }
    fun act(id: String, action: String) = mutate { active -> repository.action(active, id, action) }
    fun delete(id: String) = mutate { active -> repository.delete(active, id) }
    private fun mutate(call: suspend (SessionState.Active) -> ApiResult<app.relaxkonos.mobile.core.net.GuardianOperation>) {
        val active = owner ?: return
        if (!current(active) || mutable.value.loading || mutable.value.unknown || mutable.value.stale) return
        mutable.update { it.copy(loading = true, error = false, problemCode = null) }
        work = viewModelScope.launch {
            try { val result = call(active); if (current(active) && result !is ApiResult.Success) failure(result) }
            finally { if (current(active)) { mutable.update { it.copy(loading = false) }; if (!mutable.value.error) load(active) } }
        }
    }
    private fun failure(result: ApiResult<*>) { mutable.update { it.copy(error = true, unknown = result is ApiResult.Transport || result is ApiResult.Problem && result.status >= 500,
        problemCode = (result as? ApiResult.Problem)?.code) } }
}

@Composable
fun GuardianScreen(owner: SessionState.Active, onBack: () -> Unit, modifier: Modifier = Modifier) {
    val model: GuardianViewModel = viewModel()
    val state by model.state.collectAsState()
    val editing = model.editing
    val liveLogs by model.logObserver.state.collectAsState()
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var resumed by remember(lifecycle) { mutableStateOf(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
    DisposableEffect(lifecycle, model) {
        val listener = LifecycleEventObserver { _, _ -> resumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
        lifecycle.addObserver(listener)
        onDispose { lifecycle.removeObserver(listener); model.stopObserving() }
    }
    LaunchedEffect(owner, resumed, editing, state.selectedId) { model.observe(owner, resumed && editing == null) }
    var actionTarget by remember(owner) { mutableStateOf<Pair<GuardianWorkload, String>?>(null) }
    LaunchedEffect(owner) { model.load(owner) }
    actionTarget?.let { (target, action) ->
        val label = stringResource(when (action) { "start" -> R.string.guardian_start; "stop" -> R.string.guardian_stop; "restart" -> R.string.guardian_restart; else -> R.string.common_delete })
        AlertDialog(onDismissRequest = { actionTarget = null }, title = { Text(label) },
            text = { Text(stringResource(R.string.guardian_action_confirm, label, target.name, target.id)) },
            confirmButton = { TextButton(enabled = !state.loading && !state.unknown && !state.stale, onClick = {
                if (action == "delete") model.delete(target.id) else model.act(target.id, action); actionTarget = null
            }) { Text(label) } }, dismissButton = { TextButton(onClick = { actionTarget = null }) { Text(stringResource(R.string.common_cancel)) } })
    }
    BackHandler(enabled = editing == null && state.selectedId != null) { model.select(null) }
    if (editing != null) {
        GuardianEditor(owner, editing, state, model::cancelEdit, model::readEditor, { definition, approval -> model.save(editing, definition, approval) }, modifier)
        return
    }
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(title = stringResource(R.string.guardian_title), onBack = { if (state.selectedId != null) model.select(null) else onBack() })
        if (state.loading) Text(stringResource(R.string.guardian_loading))
        state.status?.let { status -> Text(stringResource(if (status.running) R.string.guardian_running else R.string.guardian_unavailable)) }
        if (state.stale) Text(stringResource(R.string.guardian_stale), color = MaterialTheme.colorScheme.error)
        OperationMessageDialog(if (state.loading) null else if (state.error) stringResource(guardianProblemLabel(state.problemCode)) else if (state.unknown) stringResource(R.string.guardian_unknown) else null, tone = if (state.error) StatusTone.Danger else StatusTone.Warning)
        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedButton(onClick = { model.load(owner) }, enabled = !state.loading) { Text(stringResource(R.string.common_refresh)) }
            Button(onClick = { model.beginEdit(GuardianDefinition(UUID.randomUUID().toString(), "", "", emptyList(), "", false, 15, 3, null, owner.userName, null), true) },
                enabled = state.status?.running == true && !state.loading && !state.unknown) { Text(stringResource(R.string.guardian_create)) }
        }
        BoxWithConstraints(Modifier.weight(1f).fillMaxWidth()) {
            if (maxWidth >= 840.dp && maxHeight >= 240.dp) {
                Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(Spacing.lg)) {
                    LazyColumn(Modifier.width(280.dp), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        items(state.workloads, key = { it.id }) { GuardianWorkloadRow(it, model::select) }
                    }
                    Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                        GuardianDetails(owner, state, liveLogs, model) { actionTarget = it }
                    }
                }
            } else {
                LazyColumn(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                    if (state.selectedId == null) items(state.workloads, key = { it.id }) { GuardianWorkloadRow(it, model::select) }
                    else item { GuardianDetails(owner, state, liveLogs, model) { actionTarget = it } }
                }
            }
        }
    }
}

@Composable
private fun GuardianWorkloadRow(workload: GuardianWorkload, onSelect: (String) -> Unit) {
    OutlinedButton(onClick = { onSelect(workload.id) }, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.fillMaxWidth()) {
            Text(workload.name)
            Text(guardianStateSummary(workload), style = MaterialTheme.typography.bodySmall)
        }
    }
}

@Composable
private fun GuardianDetails(owner: SessionState.Active, state: GuardianUiState, liveLogs: GuardianLogState,
    model: GuardianViewModel, onAction: (Pair<GuardianWorkload, String>) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        state.selectedId?.let { id ->
            val workload = state.workloads.firstOrNull { it.id == id }
            Text(workload?.name.orEmpty(), style = MaterialTheme.typography.titleLarge)
            workload?.let {
                Text(stringResource(R.string.guardian_detail, it.restartCount, it.healthFailureCount, it.processId?.toString() ?: "—"))
                Text(guardianStateSummary(it))
                it.lastExitCode?.let { code -> Text(stringResource(R.string.terminal_exit_code, code)) }
                if (it.lastProblemCode != null) Text(stringResource(guardianProblemLabel(it.lastProblemCode)))
                Text("${it.runAs ?: "—"} · ${it.executablePath.orEmpty()}")
            }
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                listOf("start" to R.string.guardian_start, "stop" to R.string.guardian_stop,
                    "restart" to R.string.guardian_restart).forEach { (action, label) ->
                    OutlinedButton(onClick = { workload?.let { onAction(it to action) } }, enabled = !state.loading && !state.unknown && !state.stale) { Text(stringResource(label)) }
                }
            }
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                OutlinedButton(onClick = { state.definition?.let { model.beginEdit(it, false) } }, enabled = state.definition != null && !state.loading && !state.unknown && !state.stale) { Text(stringResource(R.string.guardian_edit)) }
                OutlinedButton(onClick = { workload?.let { onAction(it to "delete") } }, enabled = !state.loading && !state.unknown && !state.stale) { Text(stringResource(R.string.common_delete)) }
                OutlinedButton(onClick = { model.select(id); model.logObserver.stop(); model.logObserver.observe(owner, id) }) { Text(stringResource(R.string.guardian_logs)) }
            }
            Text(stringResource(when (liveLogs.phase) { GuardianLogPhase.Live -> R.string.guardian_logs_live; GuardianLogPhase.Connecting -> R.string.guardian_logs_connecting; GuardianLogPhase.Failed -> R.string.guardian_logs_failed; GuardianLogPhase.Idle -> R.string.guardian_logs_paused }))
            if (liveLogs.truncated) Text(stringResource(R.string.guardian_logs_truncated))
            SelectionContainer { Column { (if (liveLogs.phase == GuardianLogPhase.Idle) boundedGuardianLogs(state.logs).first else liveLogs.logs).forEach { Text("${it.timestamp} [${it.stream}] ${it.message}", style = MaterialTheme.typography.bodySmall) } } }
        }
    }
}

@Composable
private fun guardianStateSummary(workload: GuardianWorkload): String = stringResource(R.string.guardian_state_summary,
    guardianStateLabel(workload.desiredState), guardianStateLabel(workload.actualState), guardianStateLabel(workload.healthStatus))

@Composable
private fun guardianStateLabel(value: String?): String = when (value) {
    "Running" -> stringResource(R.string.guardian_state_running)
    "Stopped" -> stringResource(R.string.guardian_state_stopped)
    "Starting" -> stringResource(R.string.guardian_state_starting)
    "Stopping" -> stringResource(R.string.guardian_state_stopping)
    "Failed" -> stringResource(R.string.guardian_state_failed)
    "CrashLoop" -> stringResource(R.string.guardian_state_crash_loop)
    "Backoff" -> stringResource(R.string.guardian_state_backoff)
    "Degraded" -> stringResource(R.string.guardian_state_degraded)
    "Healthy" -> stringResource(R.string.guardian_state_healthy)
    "Unhealthy" -> stringResource(R.string.guardian_state_unhealthy)
    "NotConfigured" -> stringResource(R.string.guardian_state_no_health)
    else -> stringResource(R.string.guardian_state_unknown, value ?: "—")
}

@Composable
private fun GuardianEditor(owner: SessionState.Active, draft: GuardianDraft, state: GuardianUiState,
    onCancel: () -> Unit, onRead: () -> Unit, onSave: (GuardianDefinition, GuardianApproval?) -> Unit, modifier: Modifier = Modifier) {
    var readConfirm by remember(draft) { mutableStateOf(false) }
    var discard by remember(draft) { mutableStateOf(false) }
    var approvalTarget by remember(draft) { mutableStateOf<GuardianDefinition?>(null) }
    var adminName by remember(draft) { mutableStateOf("") }
    var password by remember(draft) { mutableStateOf("") }
    DisposableEffect(owner, draft) { onDispose { password = "" } }
    fun close() { if (!state.loading) { if (draft.dirty(owner.serverPlatform)) discard = true else onCancel() } }
    BackHandler { close() }
    if (discard) AlertDialog(onDismissRequest = { discard = false }, title = { Text(stringResource(R.string.guardian_discard)) },
        confirmButton = { TextButton(onClick = { discard = false; onCancel() }) { Text(stringResource(R.string.guardian_discard_action)) } },
        dismissButton = { TextButton(onClick = { discard = false }) { Text(stringResource(R.string.common_cancel)) } })
    if (readConfirm) AlertDialog(onDismissRequest = { readConfirm = false }, title = { Text(stringResource(R.string.guardian_read_current)) },
        text = { Text(stringResource(R.string.guardian_read_replaces_draft)) },
        confirmButton = { TextButton(onClick = { readConfirm = false; onRead() }) { Text(stringResource(R.string.guardian_read_current)) } },
        dismissButton = { TextButton(onClick = { readConfirm = false }) { Text(stringResource(R.string.common_cancel)) } })
    approvalTarget?.let { definition -> AlertDialog(onDismissRequest = { approvalTarget = null; password = "" },
        title = { Text(stringResource(R.string.guardian_approval_notice)) }, text = { Column {
            Text("${definition.name} · ${definition.runAs}")
            OutlinedTextField(adminName, { adminName = it }, singleLine = true, label = { Text(stringResource(R.string.guardian_admin_name)) })
            OutlinedTextField(password, { password = it }, singleLine = true, visualTransformation = PasswordVisualTransformation(), label = { Text(stringResource(R.string.guardian_admin_password)) })
        } }, confirmButton = { TextButton(enabled = adminName.isNotBlank() && password.isNotEmpty() && !state.loading && !state.unknown, onClick = {
            val approval = GuardianApproval(adminName.trim(), password.toCharArray()); password = ""; approvalTarget = null; onSave(definition, approval)
        }) { Text(stringResource(R.string.common_save)) } }, dismissButton = { TextButton(onClick = { approvalTarget = null; password = "" }) { Text(stringResource(R.string.common_cancel)) } }) }
    Column(modifier.fillMaxSize().windowInsetsPadding(WindowInsets.ime).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        ScreenHeader(title = stringResource(R.string.guardian_editor), onBack = { close() })
        Column(Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OperationMessageDialog(if (state.loading) null else if (state.error) stringResource(guardianProblemLabel(state.problemCode)) else if (state.unknown) stringResource(R.string.guardian_unknown) else null, tone = if (state.error) StatusTone.Danger else StatusTone.Warning)
            if (state.unknown || state.error) OutlinedButton(onClick = { if (draft.dirty(owner.serverPlatform)) readConfirm = true else onRead() }, enabled = !state.loading) { Text(stringResource(R.string.guardian_read_current)) }
            OutlinedTextField(draft.name, { draft.name = it }, label = { Text(stringResource(R.string.guardian_name)) }, modifier = Modifier.fillMaxWidth(), enabled = !state.loading)
            RemotePathField(draft.executable, { draft.executable = it }, R.string.guardian_executable, RemotePathKind.File, modifier = Modifier.fillMaxWidth(), enabled = !state.loading)
            Text(stringResource(R.string.guardian_arguments))
            draft.arguments.forEachIndexed { index, value ->
                Row(Modifier.fillMaxWidth()) {
                    OutlinedTextField(value, { draft.arguments[index] = it }, modifier = Modifier.weight(1f), enabled = !state.loading, maxLines = 4,
                        label = { Text(stringResource(R.string.guardian_argument_number, index + 1)) })
                    val removeLabel = stringResource(R.string.guardian_remove_argument, index + 1)
                    TextButton(onClick = { draft.arguments.removeAt(index) }, enabled = !state.loading, modifier = Modifier.semantics { contentDescription = removeLabel }) { Text("×") }
                }
            }
            OutlinedButton(onClick = { draft.arguments.add("") }, enabled = !state.loading && draft.arguments.size < 128) { Text(stringResource(R.string.guardian_add_argument)) }
            RemotePathField(draft.directory, { draft.directory = it }, R.string.guardian_directory, RemotePathKind.Directory, modifier = Modifier.fillMaxWidth(), enabled = !state.loading)
            OutlinedTextField(draft.runAs, { draft.runAs = it }, label = { Text(stringResource(R.string.guardian_run_as)) }, modifier = Modifier.fillMaxWidth(), enabled = !state.loading)
            draft.initial.runAsIdentity?.let { Text(stringResource(R.string.guardian_stable_identity, it), style = MaterialTheme.typography.bodySmall) }
            Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { Checkbox(draft.enabled, { draft.enabled = it }, enabled = !state.loading); Text(stringResource(R.string.guardian_boot)) }
            OutlinedTextField(draft.stopTimeout, { draft.stopTimeout = it }, label = { Text(stringResource(R.string.guardian_stop_timeout)) }, enabled = !state.loading)
            OutlinedTextField(draft.attempts, { draft.attempts = it }, label = { Text(stringResource(R.string.guardian_restart_attempts)) }, enabled = !state.loading)
            Text(stringResource(R.string.guardian_health_type))
            Row(Modifier.horizontalScroll(rememberScrollState())) {
                listOf("" to R.string.guardian_health_none, "process" to R.string.guardian_health_process, "http" to R.string.guardian_health_http, "tcp" to R.string.guardian_health_tcp).forEach { (type, label) ->
                    TextButton(onClick = { draft.healthType = type; if (type == "process" || type.isEmpty()) draft.healthTarget = "" }, enabled = !state.loading) {
                        Text(stringResource(label) + if (type.equals(draft.healthType, true)) " ✓" else "")
                    }
                }
            }
            if (draft.healthType.isNotEmpty()) {
                if (!draft.healthType.equals("process", true)) OutlinedTextField(draft.healthTarget, { draft.healthTarget = it }, label = { Text(stringResource(R.string.guardian_health_target)) }, enabled = !state.loading)
                OutlinedTextField(draft.interval, { draft.interval = it }, label = { Text(stringResource(R.string.guardian_health_interval)) }, enabled = !state.loading)
                OutlinedTextField(draft.timeout, { draft.timeout = it }, label = { Text(stringResource(R.string.guardian_health_timeout)) }, enabled = !state.loading)
                OutlinedTextField(draft.threshold, { draft.threshold = it }, label = { Text(stringResource(R.string.guardian_health_threshold)) }, enabled = !state.loading)
            }
        }
        Button(onClick = {
            val definition = draft.definition(owner.serverPlatform)
            if (guardianApprovalRequired(owner, draft.runAs)) approvalTarget = definition else onSave(definition, null)
        }, enabled = !state.loading && !state.unknown && !state.stale && draft.valid(owner.serverPlatform), modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.common_save)) }
    }
}
