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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
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
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(
            title = stringResource(R.string.deployments_title),
            subtitle = state.owner?.let { "${it.userName} · ${it.workspaceName}" },
            onBack = onBack,
            trailing = {
                FlowRow(
                    horizontalArrangement = Arrangement.End,
                    verticalArrangement = Arrangement.spacedBy(Spacing.xs),
                ) {
                    TextButton(onClick = { showCatalog = true }, enabled = available && state.catalog is ApiResult.Success && !state.submitting) {
                        Text(stringResource(R.string.catalog_install_from_template))
                    }
                    TextButton(onClick = { viewModel.clearStagedArchive(); showCreate = true }, enabled = available && state.templates is ApiResult.Success && !state.submitting) {
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
            submission = state.submission,
            definitionSubmission = state.definitionSubmission,
            snapshot = (state.detail as? ApiResult.Success)?.value,
            diagnostics = (state.operationDiagnostics as? ApiResult.Success)?.value,
            stagedArchive = state.stagedArchive,
            archiveStaging = state.archiveStaging,
            imageTags = state.imageTags,
            imageTagsRepository = state.imageTagsRepository,
            imageTagsLoading = state.imageTagsLoading,
            onLookupImageTags = browser::lookupImageTags,
            onDismiss = { if (!state.submitting) showCreate = false },
            onImageSubmit = { definition, image -> browser.createImage(definition, image) },
            onDefinitionSubmit = { image, archive -> browser.createDefinition(image, archive) },
            onArchiveStage = viewModel::stageArchive,
            onServerArchiveStage = viewModel::stageServerArchive,
            onClearArchive = viewModel::clearStagedArchive,
            onArchiveSubmit = browser::createArchive,
        )
    }
    if (showCatalog) {
        CatalogInstallDialog(
            templates = (state.catalog as? ApiResult.Success)?.value.orEmpty(),
            capabilities = state.owner?.capabilities.orEmpty(),
            runtime = (state.runtime as? ApiResult.Success)?.value,
            submitting = state.submitting,
            onDismiss = { if (!state.submitting) showCatalog = false },
            onInstall = { template, name, fields -> browser.installCatalog(template, name, fields); showCatalog = false },
        )
    }
}

@Composable
@OptIn(ExperimentalMaterial3Api::class)
private fun CatalogInstallDialog(
    templates: List<CatalogTemplate>, capabilities: Set<String>, runtime: DeploymentRuntime?, submitting: Boolean, onDismiss: () -> Unit,
    onInstall: (CatalogTemplate, String, List<CatalogFieldValue>) -> Unit,
) {
    var selectedId by remember(templates) { mutableStateOf(templates.firstOrNull()?.id) }
    val template = templates.firstOrNull { it.id == selectedId }
    var name by remember(template) { mutableStateOf("") }
    var values by remember(template) { mutableStateOf(template?.fields?.associate { it.id to (it.defaultValue ?: "") }.orEmpty()) }
    val compatibility = template?.compatibility(capabilities, runtime)
    val supported = compatibility?.canInstall == true
    val complete = template?.fields?.all { !it.required || values[it.id].isNullOrBlank().not() } == true
    val validValues = template?.fields?.all { field ->
        field.type != "number" || values[field.id].isNullOrBlank() || values[field.id]?.toDoubleOrNull() != null
    } == true
    Dialog(
        onDismissRequest = { if (!submitting) onDismiss() },
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false),
    ) {
        Surface(modifier = Modifier.fillMaxSize().safeDrawingPadding(), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.fillMaxSize()) {
                ScreenHeader(
                    title = stringResource(R.string.catalog_title),
                    onBack = if (!submitting) onDismiss else null,
                    modifier = Modifier.padding(horizontal = Spacing.lg, vertical = Spacing.md),
                )
                Column(
                    Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = Spacing.lg),
                    verticalArrangement = Arrangement.spacedBy(Spacing.md),
                ) {
            Text(stringResource(R.string.catalog_note), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                templates.forEach { option ->
                    FilterChip(selected = option.id == selectedId, onClick = { selectedId = option.id }, label = { Text(option.purpose) })
                }
            }
            template?.let { selected ->
                Text(selected.description)
                Text(stringResource(R.string.catalog_version, selected.version), style = MaterialTheme.typography.bodySmall)
                Text(stringResource(R.string.catalog_source, selected.publisher, selected.source), style = MaterialTheme.typography.bodySmall)
                Text(stringResource(R.string.catalog_access, selected.accessPath ?: "/", selected.containerPort), style = MaterialTheme.typography.bodySmall)
                Text(stringResource(R.string.catalog_resources, selected.minimumResources.cpuCores ?: 0.0,
                    (selected.minimumResources.memoryBytes ?: 0L) / (1024L * 1024L)), style = MaterialTheme.typography.bodySmall)
                if (selected.volumes.isNotEmpty()) Text(stringResource(R.string.catalog_data, selected.volumes.joinToString { it.containerPath }), style = MaterialTheme.typography.bodySmall)
                compatibility?.blockers?.forEach { blocker ->
                    Text(catalogBlockerText(blocker), color = MaterialTheme.colorScheme.error)
                }
                OutlinedTextField(name, { name = it }, label = { Text(stringResource(R.string.deployments_name)) }, singleLine = true,
                    modifier = Modifier.fillMaxWidth())
                selected.fields.forEach { field ->
                    if (field.type == "enum") {
                        Text(field.label(), style = MaterialTheme.typography.labelLarge)
                        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            field.options.forEach { option ->
                                FilterChip(selected = values[field.id] == option, onClick = { values = values + (field.id to option) }, label = { Text(option) })
                            }
                        }
                        field.help?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                    } else {
                        OutlinedTextField(values[field.id].orEmpty(), { values = values + (field.id to it) },
                            label = { Text(field.label()) }, supportingText = field.help?.let { { Text(it) } }, singleLine = true,
                            isError = field.type == "number" && values[field.id].isNullOrBlank().not() && values[field.id]?.toDoubleOrNull() == null,
                            keyboardOptions = if (field.type == "number") KeyboardOptions(keyboardType = KeyboardType.Decimal) else KeyboardOptions.Default,
                            visualTransformation = if (field.type == "secret") PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
                            modifier = Modifier.fillMaxWidth())
                    }
                }
                Text(selected.maintenanceNotes, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
                }
                HorizontalDivider()
                FlowRow(
                    modifier = Modifier.fillMaxWidth().padding(Spacing.lg),
                    horizontalArrangement = Arrangement.End,
                    verticalArrangement = Arrangement.spacedBy(Spacing.xs),
                ) {
                    TextButton(onClick = onDismiss, enabled = !submitting) { Text(stringResource(R.string.common_cancel)) }
                    template?.let { selected ->
                        Button(onClick = { onInstall(selected, name, selected.fields.mapNotNull { field -> values[field.id]?.let { CatalogFieldValue(field.id, it) } }) },
                            enabled = supported && complete && validValues && name.isNotBlank() && !submitting) { Text(stringResource(R.string.catalog_install)) }
                    }
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
    submission: ApiResult<DeploymentOperation>?,
    definitionSubmission: ApiResult<DeploymentApplication>?,
    snapshot: DeploymentSnapshot?,
    diagnostics: DeploymentOperationDiagnostics?,
    stagedArchive: ApiResult<DeploymentArchive>?,
    archiveStaging: Boolean,
    imageTags: ApiResult<DeploymentImageTags>?,
    imageTagsRepository: String?,
    imageTagsLoading: Boolean,
    onLookupImageTags: (String) -> Unit,
    onDismiss: () -> Unit,
    onImageSubmit: (ImageDeploymentDefinition, String) -> Unit,
    onDefinitionSubmit: (ImageDeploymentDefinition?, ArchiveDeploymentDefinition?) -> Unit,
    onArchiveStage: (Uri) -> Unit,
    onServerArchiveStage: (String) -> Unit,
    onClearArchive: () -> Unit,
    onArchiveSubmit: (ArchiveDeploymentDefinition, String) -> Unit,
) {
    // A list refresh after submission must not reset the wizard while its progress step is open.
    val form = remember { DeploymentCreateForm(templates) }
    var sourceMenuExpanded by remember { mutableStateOf(false) }
    var showServerArchivePicker by remember { mutableStateOf(false) }
    var attemptedNext by remember { mutableStateOf(false) }
    val pickArchive = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            form.archiveName = ""
            onClearArchive()
            onArchiveStage(uri)
        }
    }
    LaunchedEffect(stagedArchive) {
        form.archiveName = (stagedArchive as? ApiResult.Success)?.value?.fileName.orEmpty()
    }
    val stepTitles = listOf(
        R.string.deployments_step_source, R.string.deployments_step_entry, R.string.deployments_step_runtime,
        R.string.deployments_step_configuration, R.string.deployments_step_proxy, R.string.deployments_step_preview,
        R.string.deployments_step_progress,
    )
    val problem = if (attemptedNext) form.problemAt(form.step) else null
    Dialog(
        onDismissRequest = { if (!submitting) onDismiss() },
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false),
    ) {
        Surface(modifier = Modifier.fillMaxSize().safeDrawingPadding(), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.fillMaxSize()) {
                ScreenHeader(
                    title = stringResource(R.string.deployments_create_title),
                    subtitle = "${form.step + 1} / 7 · ${stringResource(stepTitles[form.step])}",
                    onBack = if (!submitting) onDismiss else null,
                    modifier = Modifier.padding(horizontal = Spacing.lg, vertical = Spacing.md),
                )
                Column(
                    Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = Spacing.lg),
                    verticalArrangement = Arrangement.spacedBy(Spacing.md),
                ) {
                    Text(stringResource(stepTitles[form.step]), style = MaterialTheme.typography.titleMedium)
                    when (form.step) {
                        0 -> {
                            OutlinedTextField(form.name, { form.name = it }, label = { Text(stringResource(R.string.deployments_name)) },
                                singleLine = true, modifier = Modifier.fillMaxWidth())
                            Text(stringResource(R.string.deployments_name_rule), style = MaterialTheme.typography.bodySmall)
                            ExposedDropdownMenuBox(expanded = sourceMenuExpanded, onExpandedChange = { sourceMenuExpanded = it }) {
                                OutlinedTextField(value = form.template?.let { label(it.sourceKind) }.orEmpty(), onValueChange = {},
                                    readOnly = true, label = { Text(stringResource(R.string.deployments_source_selector)) },
                                    trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = sourceMenuExpanded) },
                                    modifier = Modifier.menuAnchor(ExposedDropdownMenuAnchorType.PrimaryNotEditable).fillMaxWidth())
                                ExposedDropdownMenu(expanded = sourceMenuExpanded, onDismissRequest = { sourceMenuExpanded = false }) {
                                    templates.forEach { option -> DropdownMenuItem(text = { Text(label(option.sourceKind)) }, onClick = {
                                        form.selectSource(option.sourceKind)
                                        onClearArchive()
                                        sourceMenuExpanded = false
                                    }) }
                                }
                            }
                            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                listOf("web" to R.string.deployment_web, "worker" to R.string.deployment_worker).forEach { (kind, title) ->
                                    FilterChip(selected = form.workload == kind, onClick = { form.selectWorkload(kind) }, label = { Text(stringResource(title)) })
                                }
                            }
                        }
                        1 -> {
                            if (form.template?.requiresImageReference == true) {
                                OutlinedTextField(form.image, { form.image = it }, label = { Text(stringResource(R.string.deployments_image_reference)) },
                                    supportingText = { Text(stringResource(R.string.deployments_image_version_note)) },
                                    singleLine = true, modifier = Modifier.fillMaxWidth())
                                OutlinedButton(onClick = { onLookupImageTags(form.image) }, enabled = form.image.isNotBlank() && !imageTagsLoading) {
                                    Text(stringResource(R.string.deployments_lookup_tags))
                                }
                                if (imageTagsLoading && imageTagsRepository == form.image) LinearProgressIndicator(Modifier.fillMaxWidth())
                                if (imageTagsRepository == form.image) when (val result = imageTags) {
                                    is ApiResult.Success -> if (result.value.available) {
                                        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                            result.value.tags.forEach { tag -> FilterChip(selected = form.image == tag.imageReference,
                                                onClick = { form.image = tag.imageReference }, label = { Text(tag.tag) }) }
                                        }
                                    } else Text(stringResource(R.string.deployments_tags_unavailable), style = MaterialTheme.typography.bodySmall)
                                    null -> Unit
                                    else -> Text(result.deploymentFailure().text(), color = MaterialTheme.colorScheme.error)
                                }
                            }
                            if (form.isArchive) {
                                Text(stringResource(R.string.deployments_archive_note), style = MaterialTheme.typography.bodySmall)
                                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                    OutlinedButton(onClick = { pickArchive.launch(arrayOf("application/zip", "application/java-archive", "application/octet-stream")) }) {
                                        Text(stringResource(R.string.deployments_choose_archive))
                                    }
                                    OutlinedButton(onClick = { showServerArchivePicker = true }) {
                                        Text(stringResource(R.string.deployments_choose_server_archive))
                                    }
                                }
                                if (archiveStaging) {
                                    LinearProgressIndicator(Modifier.fillMaxWidth())
                                    TextButton(onClick = onClearArchive) { Text(stringResource(R.string.common_cancel)) }
                                }
                                if (form.archiveName.isNotBlank()) Text(form.archiveName)
                                if (stagedArchive != null && stagedArchive !is ApiResult.Success)
                                    Text(stagedArchive.deploymentFailure().text(), color = MaterialTheme.colorScheme.error)
                                OutlinedTextField(form.baseImage, { form.baseImage = it }, label = { Text(stringResource(R.string.deployments_base_image)) },
                                    singleLine = true, modifier = Modifier.fillMaxWidth())
                                OutlinedTextField(form.programEntry, { form.programEntry = it }, label = { Text(stringResource(R.string.deployments_program_entry)) },
                                    singleLine = true, modifier = Modifier.fillMaxWidth())
                                OutlinedTextField(form.arguments, { form.arguments = it }, label = { Text(stringResource(R.string.deployments_arguments)) },
                                    minLines = 2, modifier = Modifier.fillMaxWidth())
                                if (form.template?.supportsSelfContained == true) {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Checkbox(checked = form.selfContained, onCheckedChange = { form.selfContained = it })
                                        Text(stringResource(R.string.deployments_self_contained))
                                    }
                                }
                            }
                            if (form.isArchive) OutlinedTextField(form.runtimeVersion, { form.runtimeVersion = it },
                                label = { Text(stringResource(R.string.deployments_runtime_version)) },
                                singleLine = true, modifier = Modifier.fillMaxWidth())
                        }
                        2 -> {
                            OutlinedTextField(form.port, { form.port = it }, label = { Text(stringResource(R.string.deployments_container_port)) },
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), singleLine = true, modifier = Modifier.fillMaxWidth())
                            OutlinedTextField(form.hostPort, { form.hostPort = it }, label = { Text(stringResource(R.string.deployments_host_port)) },
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), singleLine = true, modifier = Modifier.fillMaxWidth())
                            OutlinedTextField(form.bindAddress, { form.bindAddress = it }, label = { Text(stringResource(R.string.deployments_bind_address)) },
                                singleLine = true, modifier = Modifier.fillMaxWidth())
                            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                                listOf("http", "process").forEach { level ->
                                    FilterChip(selected = form.readiness == level, onClick = { form.readiness = level }, label = { Text(label(level)) })
                                }
                            }
                            if (form.readiness == "http") OutlinedTextField(form.healthPath, { form.healthPath = it },
                                label = { Text(stringResource(R.string.deployments_health_path)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                            Text(stringResource(R.string.deployments_host_port_note), style = MaterialTheme.typography.bodySmall)
                        }
                        3 -> {
                            Text(stringResource(R.string.deployments_configuration), style = MaterialTheme.typography.titleSmall)
                            form.configuration.forEach { entry ->
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Text(if (entry.isSecret) stringResource(R.string.deployments_secret_configured, entry.name)
                                        else "${entry.name}=${entry.value}", style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                                    TextButton(onClick = { form.configuration.remove(entry) }) { Text(stringResource(R.string.common_delete)) }
                                }
                            }
                            OutlinedTextField(form.configurationName, { form.configurationName = it }, label = { Text(stringResource(R.string.deployments_configuration_name)) },
                                singleLine = true, modifier = Modifier.fillMaxWidth())
                            OutlinedTextField(form.configurationValue, { form.configurationValue = it }, label = { Text(stringResource(R.string.deployments_configuration_value)) },
                                singleLine = true, visualTransformation = if (form.configurationSecret) PasswordVisualTransformation()
                                    else androidx.compose.ui.text.input.VisualTransformation.None, modifier = Modifier.fillMaxWidth())
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Checkbox(checked = form.configurationSecret, onCheckedChange = { form.configurationSecret = it })
                                Text(stringResource(R.string.deployments_configuration_secret))
                                TextButton(onClick = form::addConfiguration, enabled = form.configurationName.isNotBlank()) {
                                    Text(stringResource(R.string.deployments_configuration_add))
                                }
                            }
                            OutlinedTextField(form.volumes, { form.volumes = it }, label = { Text(stringResource(R.string.deployments_volumes)) },
                                supportingText = { Text(stringResource(R.string.deployments_volumes_hint)) }, minLines = 2, modifier = Modifier.fillMaxWidth())
                            OutlinedTextField(form.cpuCores, { form.cpuCores = it }, label = { Text(stringResource(R.string.deployments_cpu)) },
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal), singleLine = true, modifier = Modifier.fillMaxWidth())
                            OutlinedTextField(form.memoryMegabytes, { form.memoryMegabytes = it }, label = { Text(stringResource(R.string.deployments_memory)) },
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), singleLine = true, modifier = Modifier.fillMaxWidth())
                            OutlinedTextField(form.pidsLimit, { form.pidsLimit = it }, label = { Text(stringResource(R.string.deployments_pids)) },
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), singleLine = true, modifier = Modifier.fillMaxWidth())
                        }
                        4 -> {
                            OutlinedTextField(form.siteId, { form.siteId = it }, label = { Text(stringResource(R.string.deployments_site)) },
                                singleLine = true, modifier = Modifier.fillMaxWidth())
                            Text(stringResource(R.string.deployments_site_note), style = MaterialTheme.typography.bodySmall)
                        }
                        5 -> {
                            SectionCard(title = stringResource(R.string.deployments_step_preview)) {
                                Text(form.name)
                                Text("${label(form.sourceKind)} · ${if (form.isArchive) form.archiveName else form.image}")
                                Text("${form.bindAddress}:${form.hostPort.ifBlank { "—" }} → ${form.port}")
                                Text("${form.readiness} ${if (form.readiness == "http") form.healthPath else ""}")
                                Text(stringResource(R.string.deployments_preview_counts, form.volumes.lines().count { it.isNotBlank() },
                                    form.configuration.count { !it.isSecret }, form.configuration.count { it.isSecret }))
                                Text(form.siteId.ifBlank { "—" })
                            }
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Checkbox(checked = form.deployNow, onCheckedChange = { form.deployNow = it })
                                Text(stringResource(R.string.deployments_deploy_now))
                            }
                            Text(stringResource(R.string.deployments_replacement_note), style = MaterialTheme.typography.bodySmall)
                        }
                        6 -> {
                            if (submitting) LinearProgressIndicator(Modifier.fillMaxWidth())
                            when (val result = submission) {
                                null -> if (form.deployNow) Text(stringResource(R.string.common_loading))
                                is ApiResult.Success -> {
                                    val operation = snapshot?.operations?.firstOrNull { it.operationId == result.value.operationId }
                                        ?: snapshot?.activeOperation?.takeIf { it.operationId == result.value.operationId } ?: result.value
                                    Text("${label(operation.state)} · ${label(operation.stage)}")
                                    operation.progress?.let { LinearProgressIndicator(progress = { it / 100f }, modifier = Modifier.fillMaxWidth()) }
                                    operation.problemCode?.let { Text(deploymentProblem(it).text(), color = MaterialTheme.colorScheme.error) }
                                    Text(stringResource(R.string.deployments_operation_id, operation.operationId), style = MaterialTheme.typography.bodySmall)
                                    diagnostics?.takeIf { it.operationId == operation.operationId }?.let { output ->
                                        if (output.lines.isNotEmpty()) SectionCard(title = stringResource(R.string.deployments_operation_output)) {
                                            Text(output.lines.joinToString("\n"), style = MaterialTheme.typography.bodySmall)
                                            if (output.truncated) Text(stringResource(R.string.deployments_logs_truncated))
                                        }
                                    }
                                }
                                else -> Text(result.deploymentFailure().text(), color = MaterialTheme.colorScheme.error)
                            }
                            when (val saved = definitionSubmission) {
                                is ApiResult.Success -> Text(stringResource(R.string.deployments_definition_saved, saved.value.name))
                                null -> Unit
                                else -> Text(saved.deploymentFailure().text(), color = MaterialTheme.colorScheme.error)
                            }
                        }
                    }
                    if (problem != null) Text(stringResource(R.string.deployments_step_error, wizardProblemField(problem)), color = MaterialTheme.colorScheme.error)
                }
                HorizontalDivider()
                FlowRow(modifier = Modifier.fillMaxWidth().padding(Spacing.lg), horizontalArrangement = Arrangement.End,
                    verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                    if (form.step in 1..5) TextButton(onClick = { form.back(); attemptedNext = false }) { Text(stringResource(R.string.common_back)) }
                    if (form.step < 5) Button(onClick = { attemptedNext = !form.next() }, enabled = !submitting && !archiveStaging) {
                        Text(stringResource(R.string.deployments_next))
                    }
                    if (form.step == 5) Button(onClick = {
                        val firstProblem = (0..4).firstOrNull { form.problemAt(it) != null }
                        if (firstProblem != null) {
                            form.goTo(firstProblem)
                            attemptedNext = true
                        } else {
                            form.showProgress()
                            if (!form.deployNow) {
                                if (form.isArchive) onDefinitionSubmit(null, form.archiveDefinition())
                                else onDefinitionSubmit(form.imageDefinition(), null)
                            } else if (form.isArchive) {
                                val reference = (stagedArchive as? ApiResult.Success)?.value?.referenceId
                                if (reference != null) onArchiveSubmit(form.archiveDefinition(), reference)
                            } else onImageSubmit(form.imageDefinition(), form.image)
                        }
                    }, enabled = !submitting) { Text(stringResource(if (form.deployNow) R.string.deployments_create_and_deploy else R.string.deployments_save_definition)) }
                    if (form.step == 6) TextButton(onClick = onDismiss, enabled = !submitting) { Text(stringResource(R.string.common_close)) }
                }
            }
        }
    }
    if (showServerArchivePicker) ServerArchivePicker(
        onDismiss = { showServerArchivePicker = false },
        onSelect = { path ->
            form.archiveName = ""
            onClearArchive()
            onServerArchiveStage(path)
            showServerArchivePicker = false
        },
    )
}

@Composable
private fun wizardProblemField(problem: String): String = stringResource(when (problem) {
    "source" -> R.string.deployments_source_selector
    "name" -> R.string.deployments_name
    "image" -> R.string.deployments_image_reference
    "archive" -> R.string.deployments_choose_archive
    "entry" -> R.string.deployments_program_entry
    "runtime" -> R.string.deployments_runtime_version
    "arguments" -> R.string.deployments_arguments
    "containerPort" -> R.string.deployments_container_port
    "hostPort" -> R.string.deployments_host_port
    "healthPath" -> R.string.deployments_health_path
    "volumes" -> R.string.deployments_volumes
    "cpu" -> R.string.deployments_cpu
    "memory" -> R.string.deployments_memory
    "pids" -> R.string.deployments_pids
    "site" -> R.string.deployments_site
    else -> R.string.deployments_configuration
})

@Composable
private fun ServerArchivePicker(onDismiss: () -> Unit, onSelect: (String) -> Unit) {
    val container = appContainer()
    var path by remember { mutableStateOf("") }
    var listing by remember { mutableStateOf<ApiResult<DirectoryListing>?>(null) }

    LaunchedEffect(path) {
        listing = container.files.list(path, container.elevationAnswers)
    }

    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(
            usePlatformDefaultWidth = false,
            decorFitsSystemWindows = false,
        ),
    ) {
        Surface(
            modifier = Modifier.fillMaxSize().safeDrawingPadding(),
            color = MaterialTheme.colorScheme.surface,
        ) {
        Column(
            Modifier.fillMaxSize().padding(Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Spacing.sm),
        ) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    stringResource(R.string.deployments_server_archive_title),
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.weight(1f),
                )
                TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) }
            }
            Text(path.ifBlank { "/" }, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (path.isNotBlank()) {
                TextButton(onClick = { path = container.files.navigationParentOf(path) }) {
                    Text(stringResource(R.string.common_back))
                }
            }
            when (val result = listing) {
                null -> LinearProgressIndicator(Modifier.fillMaxWidth())
                is ApiResult.Success -> {
                    val entries = result.value.entries.filter { it.isDirectory || isDeploymentArchive(it.name) }
                    if (entries.isEmpty()) {
                        Text(stringResource(R.string.deployments_server_archive_empty), style = MaterialTheme.typography.bodyMedium)
                    } else {
                        LazyColumn(Modifier.weight(1f)) {
                            items(entries, key = { it.path }) { entry ->
                                TextButton(
                                    onClick = {
                                        if (entry.isDirectory) path = entry.path else onSelect(entry.path)
                                    },
                                    modifier = Modifier.fillMaxWidth(),
                                ) {
                                    Text(if (entry.isDirectory) "${entry.name}/" else entry.name, modifier = Modifier.weight(1f))
                                }
                            }
                        }
                    }
                }
                else -> Text(result.deploymentFailure().text(), color = MaterialTheme.colorScheme.error)
            }
        }
        }
    }
}

private fun isDeploymentArchive(name: String): Boolean = name.lowercase().let { it.endsWith(".zip") || it.endsWith(".jar") }

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
                        if (app.catalogTemplateId != null && app.catalogTemplateVersion != null) {
                            Text(stringResource(R.string.catalog_instance_version, app.catalogTemplateId, app.catalogTemplateVersion))
                        }
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

@Composable
private fun catalogBlockerText(blocker: CatalogInstallBlocker): String = stringResource(
    when (blocker) {
        CatalogInstallBlocker.UnsupportedSchema -> R.string.catalog_blocked_schema
        CatalogInstallBlocker.UntrustedSource -> R.string.catalog_blocked_trust
        CatalogInstallBlocker.Withdrawn -> R.string.catalog_blocked_withdrawn
        CatalogInstallBlocker.MissingCapability -> R.string.catalog_blocked_capability
        CatalogInstallBlocker.RuntimeUnavailable -> R.string.catalog_blocked_runtime
        CatalogInstallBlocker.UnsupportedPlatform -> R.string.catalog_blocked_platform
        CatalogInstallBlocker.InvalidField -> R.string.catalog_blocked_field
    },
)

private const val INITIAL_LOG_TAIL = 20
private const val MAXIMUM_LOG_TAIL = 1_000
private const val LOG_TAIL_GROWTH = 5

private fun nextLogTail(current: Int): Int = (current * LOG_TAIL_GROWTH).coerceAtMost(MAXIMUM_LOG_TAIL)

@Composable
private fun CheckedAt(millis: Long) {
    Text(stringResource(R.string.deployments_checked_at, DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.MEDIUM).format(Date(millis))),
        style = MaterialTheme.typography.bodySmall)
}
