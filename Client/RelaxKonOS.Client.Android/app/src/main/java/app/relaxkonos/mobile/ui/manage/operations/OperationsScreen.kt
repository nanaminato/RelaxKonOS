package app.relaxkonos.mobile.ui.manage.operations

import android.app.Application
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.data.PendingInstallationRequest
import app.relaxkonos.mobile.data.ObservedOperation
import app.relaxkonos.mobile.data.OperationCheck
import app.relaxkonos.mobile.data.OperationDomain
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.theme.Spacing
import java.util.Date
import java.text.DateFormat
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.Job
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

internal data class OperationsState(
    val owner: SessionState.Active? = null,
    val loading: Boolean = false,
    val items: List<ObservedOperation> = emptyList(),
    val selectedKey: Pair<OperationDomain, String>? = null,
    val diagnostics: ApiResult<List<String>>? = null,
    val cancelling: Boolean = false,
    val cancelRequested: Boolean = false,
    val hideRequested: Boolean = false,
    val error: Boolean = false,
    val pendingInstallations: List<PendingInstallationRequest> = emptyList(),
)

internal class OperationsViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    var state by mutableStateOf(OperationsState())
        private set
    private var refreshJob: Job? = null
    private var generation = 0

    fun stopObserving(owner: SessionState.Active) {
        if (state.owner !== owner) return
        generation++
        refreshJob?.cancel()
        state = OperationsState()
    }

    fun poll(owner: SessionState.Active) {
        if (state.owner !== owner || state.loading || refreshJob?.isActive == true || state.cancelling || state.cancelRequested || state.hideRequested) return
        if (state.items.any { it.check == OperationCheck.Unavailable || it.state == "queued" || it.state == "running" } || state.pendingInstallations.isNotEmpty())
            refresh(owner, quiet = true)
    }

    fun recoverInstallation(owner: SessionState.Active, id: String) {
        if (state.owner !== owner || state.loading) return
        val request = generation
        state = state.copy(loading = true)
        viewModelScope.launch {
            try {
                val result = withContext(Dispatchers.IO) { container.installations.recoverById(owner, id.trim()) }
                if (current(owner, request)) refresh(owner, result !is ApiResult.Success)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(loading = false, error = true)
            }
        }
    }

    fun refresh(owner: SessionState.Active, cancellationUnverified: Boolean = false, quiet: Boolean = false) {
        refreshJob?.cancel()
        val request = ++generation
        state = if (state.owner === owner) state.copy(loading = !quiet, cancelling = false,
            cancelRequested = false, hideRequested = false) else OperationsState(owner = owner, loading = true)
        refreshJob = viewModelScope.launch {
            try {
                val snapshot = withContext(Dispatchers.IO) { container.operationCenter.refresh(owner) }
                val items = snapshot.items
                if (current(owner, request)) {
                    state = state.copy(loading = false, items = items,
                        selectedKey = state.selectedKey?.takeIf { key -> items.any { (it.reference.domain to it.reference.operationId) == key } },
                        error = snapshot.incomplete || cancellationUnverified, pendingInstallations = snapshot.pendingInstallations)
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(loading = false, error = true)
            }
        }
    }

    fun select(owner: SessionState.Active, item: ObservedOperation) {
        if (state.owner !== owner) return
        val key = item.reference.domain to item.reference.operationId
        state = state.copy(selectedKey = key, diagnostics = null, cancelRequested = false)
        if (item.check != OperationCheck.Verified || item.reference.domain in setOf(OperationDomain.Website, OperationDomain.Installation)) return
        viewModelScope.launch {
            val result = container.operationCenter.diagnostics(owner, item)
            if (state.owner === owner && state.selectedKey == key && container.session.state.value === owner) {
                state = state.copy(diagnostics = result)
            }
        }
    }

    fun requestCancel() { state = state.copy(cancelRequested = true) }
    fun dismissCancel() { state = state.copy(cancelRequested = false) }
    fun requestHide() { state = state.copy(hideRequested = true) }
    fun dismissHide() { state = state.copy(hideRequested = false) }

    fun hide(owner: SessionState.Active, item: ObservedOperation) {
        if (state.owner !== owner) return
        val request = generation
        viewModelScope.launch {
            try {
                withContext(Dispatchers.IO) { container.operationCenter.hide(owner, item) }
                if (current(owner, request)) state = state.copy(
                    items = state.items.filterNot { it.reference == item.reference }, selectedKey = null,
                    hideRequested = false)
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(hideRequested = false, error = true)
            }
        }
    }

    fun cancel(owner: SessionState.Active, item: ObservedOperation) {
        if (state.owner !== owner || !item.cancellable) return
        val request = generation
        state = state.copy(cancelRequested = false, cancelling = true)
        viewModelScope.launch {
            try {
                val result = container.operationCenter.cancel(owner, item)
                if (current(owner, request)) refresh(owner, result !is ApiResult.Success)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(cancelling = false, error = true)
            }
        }
    }

    private fun current(owner: SessionState.Active, request: Int) =
        state.owner === owner && container.session.state.value === owner && request == generation
}

@Composable
fun OperationsScreen(
    owner: SessionState.Active,
    onBack: () -> Unit,
    startOnAlerts: Boolean = false,
    onStartOnAlertsConsumed: () -> Unit = {},
    onOpenDeployment: (String) -> Unit,
    onOpenWebsite: (String) -> Unit,
    onOpenCompose: (String) -> Unit,
    onOpenGitBuild: (String) -> Unit,
    onOpenScript: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: OperationsViewModel = viewModel()
    val state = viewModel.state
    LaunchedEffect(owner) { viewModel.refresh(owner) }
    val visible = state.owner === owner
    val selected = if (visible) state.items.firstOrNull {
        (it.reference.domain to it.reference.operationId) == state.selectedKey
    } else null
    val hasAlerts = ServerCapabilities.EVENT_ALERTS in owner.capabilities
    var tab by remember(owner) { mutableIntStateOf(if (startOnAlerts && hasAlerts) 1 else 0) }
    LaunchedEffect(startOnAlerts, hasAlerts) {
        if (startOnAlerts && hasAlerts) {
            tab = 1
            onStartOnAlertsConsumed()
        }
    }
    DisposableEffect(owner) { onDispose { viewModel.stopObserving(owner) } }
    LaunchedEffect(owner, tab) {
        while (isActive) {
            delay(5_000)
            if (tab == 0) viewModel.poll(owner)
        }
    }
    var recoverDialog by remember(owner) { mutableStateOf(false) }
    var recoverId by remember(owner) { mutableStateOf("") }
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var exportPreview by remember(owner) { mutableStateOf(false) }
    var pendingReport by remember(owner) { mutableStateOf<String?>(null) }
    var pendingOperationId by remember(owner) { mutableStateOf("") }
    var exportFailed by remember(owner) { mutableStateOf(false) }
    var exportSaved by remember(owner) { mutableStateOf(false) }
    val saveReport = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri ->
        val report = pendingReport
        pendingReport = null
        if (uri != null && report != null) scope.launch {
            exportFailed = false
            try {
                withContext(Dispatchers.IO) {
                    val output = context.contentResolver.openOutputStream(uri, "w")
                        ?: throw IllegalStateException("Unable to open diagnostic destination")
                    output.use { it.write(report.toByteArray(Charsets.UTF_8)) }
                }
                exportSaved = true
            } catch (_: Exception) {
                exportFailed = true
            }
        }
    }

    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(R.string.operations_title), onBack = onBack,
            trailing = { if (tab == 0) TextButton(onClick = { viewModel.refresh(owner) }, enabled = visible && !state.loading) {
                Text(stringResource(R.string.common_refresh))
            } })
        if (hasAlerts) TabRow(selectedTabIndex = tab) {
            Tab(selected = tab == 0, onClick = { tab = 0 }, text = { Text(stringResource(R.string.operations_tasks)) })
            Tab(selected = tab == 1, onClick = { tab = 1 }, text = { Text(stringResource(R.string.operations_alerts)) })
        }
        if (tab == 0) {
        if (!visible || state.loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        if (visible && state.error) Text(stringResource(R.string.operations_refresh_failed), color = MaterialTheme.colorScheme.error)
        if (visible && !state.loading && state.items.isEmpty() && !state.error) {
            Text(stringResource(R.string.operations_empty), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (owner.privilegedOperations) OutlinedButton(onClick = { recoverDialog = true }, enabled = visible && !state.loading) {
            Text(stringResource(R.string.installation_recover))
        }
        if (visible) state.pendingInstallations.forEach { pending ->
            Text(stringResource(R.string.installation_pending,
                installationServiceLabel(pending.service), installationKindLabel(pending.kind)),
                color = MaterialTheme.colorScheme.error)
        }
        if (visible) state.items.forEach { item ->
            ListRow(
                title = item.installation?.let { installationServiceLabel(it.service) }
                    ?: if (item.reference.domain == OperationDomain.Installation) installationReferenceLabel(item.reference.resourceId) else item.target,
                subtitle = stringResource(when (item.reference.domain) {
                    OperationDomain.Deployment -> R.string.operations_deployment
                    OperationDomain.Website -> R.string.operations_website
                    OperationDomain.Compose -> R.string.operations_compose
                    OperationDomain.GitBuild -> R.string.operations_git_build
                    OperationDomain.Script -> R.string.operations_script
                    OperationDomain.Backup -> R.string.operations_backup
                    OperationDomain.Installation -> R.string.operations_installation
                }),
                supporting = operationStatus(item),
                selected = (item.reference.domain to item.reference.operationId) == state.selectedKey,
                onClick = { viewModel.select(owner, item) },
            )
        }
        if (selected != null) {
            HorizontalDivider()
            Text(stringResource(R.string.operations_detail), style = MaterialTheme.typography.titleMedium)
            Text(selected.reference.operationId, style = MaterialTheme.typography.bodySmall)
            Text(operationStatus(selected))
            val installation = selected.installation
            if (installation != null) {
                Text(stringResource(R.string.installation_action, installationKindLabel(installation.kind)))
                Text(stringResource(R.string.operations_stage, installationStageLabel(installation.stage)))
                installation.progress?.let { Text(stringResource(R.string.installation_stage_progress, it)) }
                installation.problemCode?.let { Text(installationProblemLabel(it), color = MaterialTheme.colorScheme.error) }
            } else {
                selected.stage?.takeIf(String::isNotBlank)?.let { Text(stringResource(R.string.operations_stage, it)) }
                selected.progress?.let { Text(stringResource(R.string.operations_progress, it)) }
                selected.problemCode?.let { Text(stringResource(R.string.operations_problem, it), color = MaterialTheme.colorScheme.error) }
            }
            selected.checkedAtMillis?.let { millis ->
                Text(stringResource(R.string.operations_checked,
                    DateFormat.getDateTimeInstance().format(Date(millis))))
            }
            if (selected.check == OperationCheck.Missing) {
                Text(stringResource(R.string.operations_check_target), color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (selected.reference.domain != OperationDomain.Installation) OutlinedButton(onClick = {
                when (selected.reference.domain) {
                    OperationDomain.Deployment -> onOpenDeployment(selected.reference.resourceId)
                    OperationDomain.Website -> onOpenWebsite(selected.reference.resourceId)
                    OperationDomain.Compose -> onOpenCompose(selected.reference.resourceId)
                    OperationDomain.GitBuild -> onOpenGitBuild(selected.reference.operationId)
                    OperationDomain.Script -> onOpenScript(selected.reference.operationId)
                    OperationDomain.Backup -> onOpenDeployment(selected.reference.resourceId)
                    OperationDomain.Installation -> Unit
                }
            }) { Text(stringResource(R.string.operations_open_target)) }
            if (selected.cancellable && selected.check == OperationCheck.Verified) {
                OutlinedButton(onClick = viewModel::requestCancel, enabled = !state.cancelling) {
                    Text(stringResource(R.string.operations_request_cancel))
                }
            }
            TextButton(onClick = viewModel::requestHide) { Text(stringResource(R.string.operations_hide)) }
            val diagnostics = state.diagnostics
            if (selected.reference.domain != OperationDomain.Website && selected.reference.domain != OperationDomain.Backup && diagnostics is ApiResult.Success && diagnostics.value.isNotEmpty()) {
                Text(stringResource(R.string.operations_diagnostics), style = MaterialTheme.typography.titleSmall)
                diagnostics.value.forEach { Text(it, style = MaterialTheme.typography.bodySmall) }
            }
            if (selected.reference.domain != OperationDomain.Backup) OutlinedButton(onClick = {
                pendingReport = OperationDiagnosticReport.create(selected,
                    (diagnostics as? ApiResult.Success)?.value.orEmpty(), diagnostics is ApiResult.Success)
                pendingOperationId = selected.reference.operationId
                exportPreview = true
                exportFailed = false
                exportSaved = false
            }) { Text(stringResource(R.string.operations_export_diagnostics)) }
            if (exportFailed) Text(stringResource(R.string.operations_export_failed), color = MaterialTheme.colorScheme.error)
            if (exportSaved) Text(stringResource(R.string.operations_export_saved))
        }
        } else if (hasAlerts) AlertPanel(owner, onOpenDeployment)
    }
    if (recoverDialog) AlertDialog(
        onDismissRequest = { recoverDialog = false },
        title = { Text(stringResource(R.string.installation_recover)) },
        text = { Column {
            Text(stringResource(R.string.installation_recover_help))
            OutlinedTextField(value = recoverId, onValueChange = { recoverId = it }, singleLine = true,
                label = { Text(stringResource(R.string.installation_operation_id)) })
        } },
        confirmButton = { TextButton(onClick = { recoverDialog = false; viewModel.recoverInstallation(owner, recoverId) },
            enabled = runCatching { app.relaxkonos.mobile.core.net.InstallationRoutes.operation(recoverId.trim()) }.isSuccess) {
            Text(stringResource(R.string.common_refresh))
        } },
        dismissButton = { TextButton(onClick = { recoverDialog = false }) { Text(stringResource(R.string.common_cancel)) } },
    )
    if (selected != null && state.cancelRequested) AlertDialog(
        onDismissRequest = viewModel::dismissCancel,
        title = { Text(stringResource(R.string.operations_request_cancel)) },
        text = { Text(stringResource(R.string.operations_cancel_explanation)) },
        confirmButton = { Button(onClick = { viewModel.cancel(owner, selected) }) { Text(stringResource(R.string.common_cancel)) } },
        dismissButton = { TextButton(onClick = viewModel::dismissCancel) { Text(stringResource(R.string.common_close)) } },
    )
    if (selected != null && state.hideRequested) AlertDialog(
        onDismissRequest = viewModel::dismissHide,
        title = { Text(stringResource(R.string.operations_hide)) },
        text = { Text(stringResource(R.string.operations_hide_explanation)) },
        confirmButton = { Button(onClick = { viewModel.hide(owner, selected) }) { Text(stringResource(R.string.operations_hide)) } },
        dismissButton = { TextButton(onClick = viewModel::dismissHide) { Text(stringResource(R.string.common_cancel)) } },
    )
    if (exportPreview && pendingReport != null) AlertDialog(
        onDismissRequest = { exportPreview = false; pendingReport = null },
        title = { Text(stringResource(R.string.operations_export_diagnostics)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text(stringResource(R.string.operations_export_warning), color = MaterialTheme.colorScheme.error)
                Text(pendingReport.orEmpty(), style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = { Button(onClick = {
            exportPreview = false
            saveReport.launch("relaxkonos-operation-${pendingOperationId.filter { it.isLetterOrDigit() || it == '-' }.take(48)}.json")
        }) { Text(stringResource(R.string.operations_export_save)) } },
        dismissButton = { TextButton(onClick = { exportPreview = false; pendingReport = null }) {
            Text(stringResource(R.string.common_cancel))
        } },
    )
}

@Composable
private fun operationStatus(item: ObservedOperation): String = when (item.check) {
    OperationCheck.Unavailable -> stringResource(R.string.operations_unverified)
    OperationCheck.Missing -> stringResource(R.string.operations_missing)
    OperationCheck.Verified -> if (item.installation != null) installationStateLabel(item.installation.state) else when (item.state?.lowercase()) {
        "queued", "running" -> stringResource(R.string.operations_running)
        "cancelling" -> stringResource(R.string.scripts_cancelling)
        "succeeded", "verified" -> stringResource(R.string.operations_succeeded)
        "failed", "partialfailed", "timedout" -> stringResource(R.string.operations_failed)
        "cancelled" -> stringResource(R.string.operations_cancelled)
        "interrupted" -> stringResource(R.string.operations_interrupted)
        else -> stringResource(R.string.operations_unverified)
    }
}
