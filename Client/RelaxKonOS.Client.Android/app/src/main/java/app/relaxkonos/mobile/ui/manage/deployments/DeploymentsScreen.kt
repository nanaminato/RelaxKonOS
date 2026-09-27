package app.relaxkonos.mobile.ui.manage.deployments

import android.app.Application
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.DeploymentBrowser
import app.relaxkonos.mobile.data.DeploymentBrowserState
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

class DeploymentsViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    val browser = DeploymentBrowser(container.deployments, container.session, viewModelScope)

    fun createArchive(uri: Uri, definition: ArchiveDeploymentDefinition) {
        viewModelScope.launch(Dispatchers.IO) {
            val document = container.uploadDocuments.open(uri.toString())
            withContext(Dispatchers.Main.immediate) {
                if (document == null) browser.archiveUnavailable() else browser.createArchive(definition, document)
            }
        }
    }
}

@Composable
fun DeploymentsScreen(
    layoutState: LayoutState,
    showDetail: Boolean,
    onOpenDetail: () -> Unit,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: DeploymentsViewModel = viewModel()
    val browser = viewModel.browser
    val state by browser.state.collectAsState()
    val expanded = layoutState == LayoutState.Expanded
    val available = state.owner?.capabilities?.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS) == true
    var showCreate by remember { mutableStateOf(false) }
    var pendingArchive by remember { mutableStateOf<ArchiveDeploymentDefinition?>(null) }
    val pickArchive = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        val definition = pendingArchive
        pendingArchive = null
        if (uri != null && definition != null) {
            viewModel.createArchive(uri, definition)
            showCreate = false
        }
    }
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(
            title = stringResource(R.string.deployments_title),
            subtitle = state.owner?.let { "${it.userName} · ${it.workspaceName}" },
            onBack = onBack,
            trailing = {
                Row {
                    TextButton(onClick = { showCreate = true }, enabled = available && state.templates is ApiResult.Success && !state.submitting) {
                        Text(stringResource(R.string.deployments_create_application))
                    }
                    TextButton(onClick = browser::refresh, enabled = available && !state.loading && !state.detailLoading && !state.submitting) {
                        Text(stringResource(R.string.common_refresh))
                    }
                }
            },
        )
        if (!available) {
            EmptyHint(stringResource(R.string.error_capability_missing))
            return@Column
        }
        state.submission?.let { result ->
            when (result) {
                is ApiResult.Success -> Text(stringResource(R.string.deployments_queued, result.value.operationId), color = MaterialTheme.colorScheme.primary)
                else -> Text(result.deploymentFailure().text(), color = MaterialTheme.colorScheme.error)
            }
        }
        if (expanded) {
            Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(Spacing.lg)) {
                DeploymentList(state, { browser.select(it) }, Modifier.weight(1f))
                DeploymentDetail(state, browser, Modifier.weight(1.2f))
            }
        } else if (showDetail) {
            DeploymentDetail(state, browser, Modifier.weight(1f))
        } else {
            DeploymentList(state, { browser.select(it); onOpenDetail() }, Modifier.weight(1f))
        }
    }
    if (showCreate) {
        val templates = (state.templates as? ApiResult.Success)?.value.orEmpty()
        DeploymentCreateDialog(
            templates = templates,
            submitting = state.submitting,
            onDismiss = { if (!state.submitting) showCreate = false },
            onImageSubmit = { name, image, port, configuration ->
                browser.createImage(name, image, port, configuration)
                showCreate = false
            },
            onArchiveSubmit = { definition ->
                pendingArchive = definition
                pickArchive.launch(arrayOf("application/zip", "application/java-archive", "application/octet-stream"))
            },
        )
    }
}

@Composable
@OptIn(ExperimentalMaterial3Api::class)
private fun DeploymentCreateDialog(
    templates: List<DeploymentTemplate>,
    submitting: Boolean,
    onDismiss: () -> Unit,
    onImageSubmit: (String, String, Int, List<DeploymentConfigEntry>) -> Unit,
    onArchiveSubmit: (ArchiveDeploymentDefinition) -> Unit,
) {
    var sourceKind by remember(templates) { mutableStateOf(templates.firstOrNull()?.sourceKind.orEmpty()) }
    var name by remember { mutableStateOf("") }
    var image by remember { mutableStateOf("") }
    var port by remember { mutableStateOf(templates.firstOrNull()?.defaultContainerPort?.toString() ?: "8080") }
    var workload by remember { mutableStateOf("Web") }
    var baseImage by remember { mutableStateOf("") }
    var runtimeVersion by remember { mutableStateOf("") }
    var programEntry by remember { mutableStateOf("") }
    var selfContained by remember { mutableStateOf(false) }
    val configuration = remember { mutableStateListOf<DeploymentConfigEntry>() }
    var configurationName by remember { mutableStateOf("") }
    var configurationValue by remember { mutableStateOf("") }
    var configurationSecret by remember { mutableStateOf(false) }
    val template = templates.firstOrNull { it.sourceKind == sourceKind }
    LaunchedEffect(template?.sourceKind) {
        port = template?.defaultContainerPort?.toString() ?: "8080"
        baseImage = template?.defaultBaseImage.orEmpty()
        runtimeVersion = ""
        programEntry = ""
        selfContained = false
    }
    val parsedPort = port.toIntOrNull()?.takeIf { it in 1..65535 }
    val archive = template?.requiresArchive == true
    val valid = name.isNotBlank() && parsedPort != null && when {
        template == null -> false
        template.requiresImageReference -> image.isNotBlank()
        template.sourceKind == "PythonProject" -> programEntry.isNotBlank()
        else -> true
    }
    val archiveDefinition = template?.let {
        ArchiveDeploymentDefinition(
            sourceKind = it.sourceKind,
            name = name.trim(),
            containerPort = parsedPort ?: it.defaultContainerPort,
            workloadKind = workload,
            readinessLevel = if (workload == "Web") "Http" else "Process",
            healthCheckPath = if (workload == "Web") "/" else null,
            baseImage = baseImage.trim().ifBlank { null },
            runtimeVersion = runtimeVersion.trim().ifBlank { null },
            programEntry = programEntry.trim().ifBlank { null },
            selfContained = selfContained,
            configuration = configuration.toList(),
        )
    }
    ModalBottomSheet(
        onDismissRequest = { if (!submitting) onDismiss() },
    ) {
        Column(
            Modifier.fillMaxWidth()
                .padding(horizontal = Spacing.lg)
                .padding(bottom = Spacing.lg)
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(Spacing.md),
        ) {
            Text(stringResource(R.string.deployments_create_title), style = MaterialTheme.typography.headlineSmall)
            Text(
                stringResource(R.string.deployments_create_note),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                style = MaterialTheme.typography.bodySmall,
            )
            Surface(
                color = MaterialTheme.colorScheme.secondaryContainer,
                shape = MaterialTheme.shapes.medium,
            ) {
                Column(Modifier.padding(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text(stringResource(R.string.deployments_source), style = MaterialTheme.typography.labelLarge)
                    templates.forEach { option ->
                        FilterChip(
                            selected = sourceKind == option.sourceKind,
                            onClick = { sourceKind = option.sourceKind },
                            label = { Text(label(option.sourceKind)) },
                        )
                    }
                }
            }
            Surface(shape = MaterialTheme.shapes.medium, tonalElevation = Spacing.xs) {
                Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    OutlinedTextField(name, { name = it }, label = { Text(stringResource(R.string.deployments_name)) }, singleLine = true)
                    if (template?.requiresImageReference == true) {
                        OutlinedTextField(image, { image = it }, label = { Text(stringResource(R.string.deployments_image_reference)) }, singleLine = true)
                    }
                    OutlinedTextField(
                        port, { port = it }, label = { Text(stringResource(R.string.deployments_container_port)) }, singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), isError = port.isNotEmpty() && parsedPort == null,
                    )
                    if (archive) {
                        OutlinedTextField(baseImage, { baseImage = it }, label = { Text(stringResource(R.string.deployments_base_image)) }, singleLine = true)
                        if (template?.sourceKind in setOf("JavaJar", "DotNetPublish", "PythonProject")) {
                            OutlinedTextField(runtimeVersion, { runtimeVersion = it }, label = { Text(stringResource(R.string.deployments_runtime_version)) }, singleLine = true)
                        }
                        if (template?.sourceKind == "PythonProject") {
                            OutlinedTextField(programEntry, { programEntry = it }, label = { Text(stringResource(R.string.deployments_python_entry)) }, singleLine = true)
                        }
                        if (template?.supportsSelfContained == true) {
                            Row {
                                Checkbox(checked = selfContained, onCheckedChange = { selfContained = it })
                                Text(stringResource(R.string.deployments_self_contained))
                            }
                        }
                        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            FilterChip(selected = workload == "Web", onClick = { workload = "Web" }, label = { Text(stringResource(R.string.deployment_web)) })
                            FilterChip(selected = workload == "Worker", onClick = { workload = "Worker" }, label = { Text(stringResource(R.string.deployment_worker)) })
                        }
                    }
                    Text(stringResource(R.string.deployments_configuration), style = MaterialTheme.typography.titleSmall)
                    configuration.forEach { entry ->
                        Text(if (entry.isSecret) stringResource(R.string.deployments_secret_configured, entry.name) else "${entry.name}=${entry.value}",
                            style = MaterialTheme.typography.bodySmall)
                    }
                    OutlinedTextField(configurationName, { configurationName = it }, label = { Text(stringResource(R.string.deployments_configuration_name)) }, singleLine = true)
                    OutlinedTextField(
                        configurationValue, { configurationValue = it }, label = { Text(stringResource(R.string.deployments_configuration_value)) }, singleLine = true,
                        visualTransformation = if (configurationSecret) PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
                    )
                    Row {
                        Checkbox(checked = configurationSecret, onCheckedChange = { configurationSecret = it })
                        Text(stringResource(R.string.deployments_configuration_secret))
                        TextButton(onClick = {
                            if (configurationName.isNotBlank()) {
                                configuration.removeAll { it.name == configurationName.trim() }
                                configuration += DeploymentConfigEntry(configurationName.trim(), configurationValue, configurationSecret)
                                configurationName = ""; configurationValue = ""; configurationSecret = false
                            }
                        }, enabled = configurationName.isNotBlank()) { Text(stringResource(R.string.deployments_configuration_add)) }
                    }
                }
            }
            if (archive) {
                Text(
                    stringResource(R.string.deployments_archive_note),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                TextButton(onClick = onDismiss, enabled = !submitting) { Text(stringResource(R.string.common_cancel)) }
                Button(
                    onClick = {
                        if (archive) onArchiveSubmit(archiveDefinition!!)
                        else onImageSubmit(name.trim(), image.trim(), parsedPort!!, configuration.toList())
                    },
                    enabled = valid && !submitting,
                ) {
                    Text(stringResource(if (archive) R.string.deployments_choose_archive else R.string.deployments_create_and_deploy))
                }
            }
        }
    }
}

@Composable
private fun DeploymentList(state: DeploymentBrowserState, onSelect: (String) -> Unit, modifier: Modifier) {
    LazyColumn(modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        item { Text(stringResource(R.string.deployments_image_deploy_note), style = MaterialTheme.typography.bodySmall) }
        item {
            SectionCard(title = stringResource(R.string.deployments_runtime)) {
                when (val runtime = state.runtime) {
                    is ApiResult.Success -> {
                        if (runtime.value.isAvailable) {
                            Text(stringResource(R.string.deployments_runtime_ready))
                            Text(listOfNotNull(runtime.value.serverVersion, runtime.value.operatingSystem, runtime.value.architecture).joinToString(" · "))
                        } else {
                            Text(deploymentProblem(runtime.value.problemCode).text(), color = MaterialTheme.colorScheme.error)
                        }
                    }
                    null -> Text(stringResource(if (ServerCapabilities.DOCKER !in state.owner!!.capabilities)
                        R.string.deployments_runtime_missing else R.string.common_loading))
                    else -> Text(runtime.runtimeFailure().text(), color = MaterialTheme.colorScheme.error)
                }
            }
        }
        state.owner?.executionEligibility?.takeIf { !it.available }?.let { eligibility ->
            item { ExecutionEligibilityNotice(eligibility.reason) }
        }
        if (state.loading) item { LinearProgressIndicator(Modifier.fillMaxWidth()) }
        state.checkedAtMillis?.let { item { CheckedAt(it) } }
        when (val result = state.applications) {
            is ApiResult.Success -> {
                if (result.value.isEmpty()) item { EmptyHint(stringResource(R.string.deployments_empty)) }
                items(result.value, key = { it.id }) { application ->
                    SectionGroup {
                        ListRow(
                            title = application.name,
                            subtitle = stringResource(R.string.deployments_actual, label(application.actualState)),
                            supporting = stringResource(R.string.deployments_revision, revisionLabel(application.currentRevisionNumber)),
                            selected = state.selectedId == application.id,
                            onClick = { onSelect(application.id) },
                        )
                    }
                }
            }
            null -> Unit
            else -> item { Text(result.deploymentFailure().text(), color = MaterialTheme.colorScheme.error) }
        }
    }
}

@Composable
private fun DeploymentDetail(state: DeploymentBrowserState, browser: DeploymentBrowser, modifier: Modifier) {
    var actionToConfirm by remember { mutableStateOf<DeploymentLifecycleAction?>(null) }
    var rollbackRevision by remember { mutableStateOf<DeploymentRevision?>(null) }
    var deleteConfirmation by remember { mutableStateOf(false) }
    LazyColumn(modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        if (state.selectedId == null) item { EmptyHint(stringResource(R.string.deployments_select)) }
        if (state.detailLoading) item { LinearProgressIndicator(Modifier.fillMaxWidth()) }
        state.detailCheckedAtMillis?.let { item { CheckedAt(it) } }
        when (val result = state.detail) {
            is ApiResult.Success -> {
                val snapshot = result.value
                val app = snapshot.application
                item {
                    SectionCard(title = app.name, subtitle = stringResource(R.string.deployments_overview)) {
                        Text(stringResource(R.string.deployments_actual, label(app.actualState)))
                        Text(stringResource(R.string.deployments_desired, label(app.desiredState)))
                        Text(stringResource(R.string.deployments_revision, revisionLabel(app.currentRevisionNumber)))
                        Text(stringResource(R.string.deployments_source, label(app.sourceKind)))
                        Text(stringResource(R.string.deployments_workload, label(app.workloadKind)))
                        Text(stringResource(R.string.deployments_readiness, label(app.readinessLevel)))
                        app.containerName?.let { Text(stringResource(R.string.deployments_container, it)) }
                        Text(stringResource(R.string.deployments_ports,
                            app.hostPort?.let { "${app.bindAddress}:$it" } ?: stringResource(R.string.deployments_unpublished), app.containerPort))
                        app.domain?.let { Text(stringResource(R.string.deployments_domain, it)) }
                        if (app.driftProblemCode != null) Text(stringResource(R.string.deployments_drift), color = MaterialTheme.colorScheme.error)
                        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            if (app.actualState == "Running") {
                                TextButton(onClick = { actionToConfirm = DeploymentLifecycleAction.Stop }, enabled = !state.submitting) {
                                    Text(stringResource(R.string.deployment_stop))
                                }
                                TextButton(onClick = { actionToConfirm = DeploymentLifecycleAction.Restart }, enabled = !state.submitting) {
                                    Text(stringResource(R.string.deployment_restart))
                                }
                            } else if (app.actualState == "Stopped") {
                                TextButton(onClick = { browser.lifecycle(DeploymentLifecycleAction.Start) }, enabled = !state.submitting) {
                                    Text(stringResource(R.string.deployment_start))
                                }
                            }
                        }
                        TextButton(onClick = { deleteConfirmation = true }, enabled = !state.submitting) {
                            Text(stringResource(R.string.deployment_delete), color = MaterialTheme.colorScheme.error)
                        }
                    }
                }
                snapshot.activeOperation?.let { operation ->
                    item { OperationCard(operation, stringResource(R.string.deployments_active), if (operation.cancellable && !state.submitting) ({ browser.cancel(operation) }) else null) }
                }
                item { Text(stringResource(R.string.deployments_logs), style = MaterialTheme.typography.titleMedium) }
                if (state.logsLoading) item { LinearProgressIndicator(Modifier.fillMaxWidth()) }
                when (val logs = state.logs) {
                    is ApiResult.Success -> item {
                        SectionCard(title = stringResource(if (logs.value.truncated) R.string.deployments_logs_truncated else R.string.deployments_logs_recent)) {
                            if (logs.value.lines.isEmpty()) Text(stringResource(R.string.deployments_logs_empty))
                            logs.value.lines.forEach { Text(it, style = MaterialTheme.typography.bodySmall) }
                        }
                    }
                    null -> Unit
                    else -> item { Text(logs.deploymentFailure().text(), color = MaterialTheme.colorScheme.error) }
                }
                item { Text(stringResource(R.string.deployments_revisions), style = MaterialTheme.typography.titleMedium) }
                if (snapshot.revisions.isEmpty()) item { EmptyHint(stringResource(R.string.deployments_no_revisions)) }
                items(snapshot.revisions, key = { it.id }) { revision ->
                    SectionCard(title = stringResource(R.string.deployments_revision_number, revision.number)) {
                        Text(revision.imageReference, style = MaterialTheme.typography.bodySmall)
                        if (revision.isCurrent) {
                            Text(stringResource(R.string.deployments_current), color = MaterialTheme.colorScheme.primary)
                        } else {
                            TextButton(onClick = { rollbackRevision = revision }, enabled = !state.submitting) {
                                Text(stringResource(R.string.deployment_rollback))
                            }
                        }
                    }
                }
                item { Text(stringResource(R.string.deployments_operations), style = MaterialTheme.typography.titleMedium) }
                if (snapshot.operations.isEmpty()) item { EmptyHint(stringResource(R.string.deployments_no_operations)) }
                items(snapshot.operations.filter { it.operationId != snapshot.activeOperation?.operationId }, key = { it.operationId }) {
                    OperationCard(it, label(it.kind))
                }
            }
            null -> Unit
            else -> item { Text(result.deploymentFailure().text(), color = MaterialTheme.colorScheme.error) }
        }
    }
    actionToConfirm?.let { action ->
        AlertDialog(
            onDismissRequest = { if (!state.submitting) actionToConfirm = null },
            title = { Text(stringResource(R.string.deployments_confirm_title)) },
            text = { Text(stringResource(R.string.deployments_confirm_action, label(action.name))) },
            confirmButton = {
                TextButton(onClick = { browser.lifecycle(action); actionToConfirm = null }, enabled = !state.submitting) {
                    Text(stringResource(R.string.deployments_confirm))
                }
            },
            dismissButton = { TextButton(onClick = { actionToConfirm = null }, enabled = !state.submitting) { Text(stringResource(R.string.common_cancel)) } },
        )
    }
    rollbackRevision?.let { revision ->
        AlertDialog(
            onDismissRequest = { if (!state.submitting) rollbackRevision = null },
            title = { Text(stringResource(R.string.deployments_rollback_title)) },
            text = { Text(stringResource(R.string.deployments_rollback_note, revision.number)) },
            confirmButton = {
                TextButton(onClick = { browser.rollback(revision); rollbackRevision = null }, enabled = !state.submitting) {
                    Text(stringResource(R.string.deployment_rollback))
                }
            },
            dismissButton = { TextButton(onClick = { rollbackRevision = null }, enabled = !state.submitting) { Text(stringResource(R.string.common_cancel)) } },
        )
    }
    if (deleteConfirmation) {
        AlertDialog(
            onDismissRequest = { if (!state.submitting) deleteConfirmation = false },
            title = { Text(stringResource(R.string.deployments_delete_title)) },
            text = { Text(stringResource(R.string.deployments_delete_note)) },
            confirmButton = {
                TextButton(onClick = { browser.delete(); deleteConfirmation = false }, enabled = !state.submitting) {
                    Text(stringResource(R.string.deployment_delete))
                }
            },
            dismissButton = { TextButton(onClick = { deleteConfirmation = false }, enabled = !state.submitting) { Text(stringResource(R.string.common_cancel)) } },
        )
    }
}

@Composable
private fun OperationCard(operation: DeploymentOperation, title: String, onCancel: (() -> Unit)? = null) {
    SectionCard(title = title) {
        Text(stringResource(R.string.deployments_operation_state, label(operation.state)))
        Text(stringResource(R.string.deployments_stage, label(operation.stage)))
        Text(stringResource(R.string.deployments_operation_id, operation.operationId), style = MaterialTheme.typography.bodySmall)
        operation.problemCode?.let { Text(deploymentProblem(it).text(), color = MaterialTheme.colorScheme.error) }
        if (operation.recoveryProblemCode != null) Text(stringResource(R.string.deployments_recovery), color = MaterialTheme.colorScheme.error)
        onCancel?.let { TextButton(onClick = it) { Text(stringResource(R.string.common_cancel)) } }
    }
}

@Composable
private fun label(value: String): String = stringResource(deploymentLabel(value))

@Composable
private fun revisionLabel(number: Int?): String = number?.toString() ?: stringResource(R.string.deployments_no_revision)

@Composable
private fun CheckedAt(millis: Long) {
    Text(stringResource(R.string.deployments_checked_at, DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.MEDIUM).format(Date(millis))),
        style = MaterialTheme.typography.bodySmall)
}
