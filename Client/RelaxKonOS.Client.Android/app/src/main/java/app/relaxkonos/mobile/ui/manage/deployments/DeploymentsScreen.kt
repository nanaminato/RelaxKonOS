package app.relaxkonos.mobile.ui.manage.deployments

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
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.DeploymentBrowser
import app.relaxkonos.mobile.data.DeploymentBrowserState
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

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
    var showCatalog by remember { mutableStateOf(false) }
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
                    TextButton(onClick = { showCatalog = true }, enabled = available && state.catalog is ApiResult.Success && !state.submitting) {
                        Text(stringResource(R.string.catalog_install_from_template))
                    }
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
    if (showCatalog) {
        CatalogInstallDialog(
            templates = (state.catalog as? ApiResult.Success)?.value.orEmpty(),
            capabilities = state.owner?.capabilities.orEmpty(),
            submitting = state.submitting,
            onDismiss = { if (!state.submitting) showCatalog = false },
            onInstall = { template, name, fields -> browser.installCatalog(template, name, fields); showCatalog = false },
        )
    }
}

@Composable
@OptIn(ExperimentalMaterial3Api::class)
private fun CatalogInstallDialog(
    templates: List<CatalogTemplate>, capabilities: Set<String>, submitting: Boolean, onDismiss: () -> Unit,
    onInstall: (CatalogTemplate, String, List<CatalogFieldValue>) -> Unit,
) {
    var selectedId by remember(templates) { mutableStateOf(templates.firstOrNull()?.id) }
    val template = templates.firstOrNull { it.id == selectedId }
    var name by remember(template) { mutableStateOf("") }
    var values by remember(template) { mutableStateOf(template?.fields?.associate { it.id to (it.defaultValue ?: "") }.orEmpty()) }
    val supported = template?.let { it.schemaVersion == "1" && it.requiredCapabilities.all(capabilities::contains) && !it.withdrawn } == true
    val complete = template?.fields?.all { !it.required || values[it.id].isNullOrBlank().not() } == true
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(Modifier.fillMaxWidth().padding(horizontal = Spacing.lg).padding(bottom = Spacing.lg).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
            Text(stringResource(R.string.catalog_title), style = MaterialTheme.typography.headlineSmall)
            Text(stringResource(R.string.catalog_note), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            templates.forEach { option -> FilterChip(selected = option.id == selectedId, onClick = { selectedId = option.id }, label = { Text(option.purpose) }) }
            template?.let { selected ->
                Text(selected.description)
                Text(stringResource(R.string.catalog_version, selected.version), style = MaterialTheme.typography.bodySmall)
                Text(stringResource(R.string.catalog_access, selected.accessPath ?: "/", selected.containerPort), style = MaterialTheme.typography.bodySmall)
                if (selected.volumes.isNotEmpty()) Text(stringResource(R.string.catalog_data, selected.volumes.joinToString { it.containerPath }), style = MaterialTheme.typography.bodySmall)
                if (!supported) Text(stringResource(R.string.catalog_unsupported), color = MaterialTheme.colorScheme.error)
                OutlinedTextField(name, { name = it }, label = { Text(stringResource(R.string.deployments_name)) }, singleLine = true)
                selected.fields.forEach { field ->
                    OutlinedTextField(values[field.id].orEmpty(), { values = values + (field.id to it) },
                        label = { Text(field.label()) }, supportingText = field.help?.let { { Text(it) } }, singleLine = true,
                        visualTransformation = if (field.type == "secret") PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None)
                }
                Text(selected.maintenanceNotes, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                    TextButton(onClick = onDismiss, enabled = !submitting) { Text(stringResource(R.string.common_cancel)) }
                    Button(onClick = { onInstall(selected, name, selected.fields.mapNotNull { field -> values[field.id]?.let { CatalogFieldValue(field.id, it) } }) },
                        enabled = supported && complete && name.isNotBlank() && !submitting) { Text(stringResource(R.string.catalog_install)) }
                }
            }
        }
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
    val form = remember(templates) { DeploymentCreateForm(templates) }
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
                            selected = form.sourceKind == option.sourceKind,
                            onClick = { form.selectSource(option.sourceKind) },
                            label = { Text(label(option.sourceKind)) },
                        )
                    }
                }
            }
            Surface(shape = MaterialTheme.shapes.medium, tonalElevation = Spacing.xs) {
                Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    OutlinedTextField(form.name, { form.name = it }, label = { Text(stringResource(R.string.deployments_name)) }, singleLine = true)
                    if (form.template?.requiresImageReference == true) {
                        OutlinedTextField(form.image, { form.image = it }, label = { Text(stringResource(R.string.deployments_image_reference)) }, singleLine = true)
                    }
                    OutlinedTextField(
                        form.port, { form.port = it }, label = { Text(stringResource(R.string.deployments_container_port)) }, singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), isError = form.port.isNotEmpty() && form.parsedPort == null,
                    )
                    if (form.isArchive) {
                        OutlinedTextField(form.baseImage, { form.baseImage = it }, label = { Text(stringResource(R.string.deployments_base_image)) }, singleLine = true)
                        if (form.template?.sourceKind in setOf("javaJar", "dotNetPublish", "pythonProject")) {
                            OutlinedTextField(form.runtimeVersion, { form.runtimeVersion = it }, label = { Text(stringResource(R.string.deployments_runtime_version)) }, singleLine = true)
                        }
                        if (form.template?.sourceKind == "pythonProject") {
                            OutlinedTextField(form.programEntry, { form.programEntry = it }, label = { Text(stringResource(R.string.deployments_python_entry)) }, singleLine = true)
                        }
                        if (form.template?.supportsSelfContained == true) {
                            Row {
                                Checkbox(checked = form.selfContained, onCheckedChange = { form.selfContained = it })
                                Text(stringResource(R.string.deployments_self_contained))
                            }
                        }
                        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            FilterChip(selected = form.workload == "web", onClick = { form.workload = "web" }, label = { Text(stringResource(R.string.deployment_web)) })
                            FilterChip(selected = form.workload == "worker", onClick = { form.workload = "worker" }, label = { Text(stringResource(R.string.deployment_worker)) })
                        }
                    }
                    Text(stringResource(R.string.deployments_configuration), style = MaterialTheme.typography.titleSmall)
                    form.configuration.forEach { entry ->
                        Text(if (entry.isSecret) stringResource(R.string.deployments_secret_configured, entry.name) else "${entry.name}=${entry.value}",
                            style = MaterialTheme.typography.bodySmall)
                    }
                    OutlinedTextField(form.configurationName, { form.configurationName = it }, label = { Text(stringResource(R.string.deployments_configuration_name)) }, singleLine = true)
                    OutlinedTextField(
                        form.configurationValue, { form.configurationValue = it }, label = { Text(stringResource(R.string.deployments_configuration_value)) }, singleLine = true,
                        visualTransformation = if (form.configurationSecret) PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
                    )
                    Row {
                        Checkbox(checked = form.configurationSecret, onCheckedChange = { form.configurationSecret = it })
                        Text(stringResource(R.string.deployments_configuration_secret))
                        TextButton(onClick = form::addConfiguration, enabled = form.configurationName.isNotBlank()) { Text(stringResource(R.string.deployments_configuration_add)) }
                    }
                }
            }
            if (form.isArchive) {
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
                        if (form.isArchive) onArchiveSubmit(form.archiveDefinition()!!)
                        else onImageSubmit(form.name.trim(), form.image.trim(), form.parsedPort!!, form.imageConfiguration())
                    },
                    enabled = form.canSubmit && !submitting,
                ) {
                    Text(stringResource(if (form.isArchive) R.string.deployments_choose_archive else R.string.deployments_create_and_deploy))
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
                            if (app.actualState == "running") {
                                TextButton(onClick = { actionToConfirm = DeploymentLifecycleAction.Stop }, enabled = !state.submitting) {
                                    Text(stringResource(R.string.deployment_stop))
                                }
                                TextButton(onClick = { actionToConfirm = DeploymentLifecycleAction.Restart }, enabled = !state.submitting) {
                                    Text(stringResource(R.string.deployment_restart))
                                }
                            } else if (app.actualState == "stopped") {
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
                            state.loadedLogTail?.takeIf { logs.value.truncated && it < MAXIMUM_LOG_TAIL }?.let { tail ->
                                TextButton(onClick = browser::loadMoreLogs, enabled = !state.logsLoading) {
                                    Text(stringResource(R.string.deployments_logs_load_more, nextLogTail(tail)))
                                }
                            }
                        }
                    }
                    null -> item {
                        SectionCard(title = stringResource(R.string.deployments_logs_recent)) {
                            Text(stringResource(R.string.deployments_logs_on_demand), style = MaterialTheme.typography.bodySmall)
                            TextButton(onClick = { browser.loadLogs() }, enabled = !state.logsLoading) {
                                Text(stringResource(R.string.deployments_logs_load_initial, INITIAL_LOG_TAIL))
                            }
                        }
                    }
                    else -> item {
                        Text(logs.deploymentFailure().text(), color = MaterialTheme.colorScheme.error)
                        TextButton(onClick = { browser.loadLogs() }, enabled = !state.logsLoading) {
                            Text(stringResource(R.string.deployments_logs_retry))
                        }
                    }
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

private const val INITIAL_LOG_TAIL = 20
private const val MAXIMUM_LOG_TAIL = 1_000
private const val LOG_TAIL_GROWTH = 5

private fun nextLogTail(current: Int): Int = (current * LOG_TAIL_GROWTH).coerceAtMost(MAXIMUM_LOG_TAIL)

@Composable
private fun CheckedAt(millis: Long) {
    Text(stringResource(R.string.deployments_checked_at, DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.MEDIUM).format(Date(millis))),
        style = MaterialTheme.typography.bodySmall)
}
