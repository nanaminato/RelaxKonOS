package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.ui.common.rememberUsageOpenDocument

import app.relaxkonos.mobile.ui.common.RefreshProgressIndicator

import app.relaxkonos.mobile.ui.common.ActionLabel
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.theme.Spacing

/** Revision input is independent of definition editing and never receives plaintext secrets. */
@Composable
internal fun DeploymentRevisionDialog(
    owner: SessionState.Active, initial: DeploymentSnapshot, template: DeploymentTemplate?,
    stagedArchive: ApiResult<DeploymentArchive>?, archiveStaging: Boolean,
    onArchiveStage: (Uri) -> Unit, onServerArchiveStage: (String) -> Unit, onClearArchive: () -> Unit,
    onDismiss: () -> Unit, onAccepted: (DeploymentOperation) -> Unit,
) {
    val container = appContainer()
    val scope = rememberCoroutineScope()
    val editor = remember(DeploymentOwnerKey(owner), initial.application.id, scope) { DeploymentRevisionEditor(container, owner, initial, scope) }
    DisposableEffect(editor) { onDispose { editor.close() } }
    with(editor) {
        val archive = (stagedArchive as? ApiResult.Success)?.value
        val source = DeploymentRevisionSource(
            imageReference = image.trim().takeIf { baseline.sourceKind == "image" },
            archiveReferenceId = archive?.referenceId.takeIf { baseline.sourceKind != "image" },
            baseImage = baseImage.trim().ifBlank { null }, runtimeVersion = runtime.trim().ifBlank { null },
            programEntry = entry.trim().ifBlank { null }, arguments = arguments.toList(), selfContained = selfContained,
        )
        val expired = archive?.expiresAtMillis?.let { it <= System.currentTimeMillis() } == true
        val supported = template != null && (template.requiresImageReference || template.requiresArchive)
        val editable = !busy && !unknown && result !is ApiResult.Success && snapshot.activeOperation == null
        val pickerLauncher = rememberLauncherForActivityResult(rememberUsageOpenDocument("DeploymentRevisionDialog.revision-archive")) { uri ->
            if (uri != null) { onClearArchive(); onArchiveStage(uri) }
        }

        Dialog(onDismissRequest = { if (!busy) onDismiss() }, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
            Surface(Modifier.fillMaxSize().safeDrawingPadding(), color = MaterialTheme.colorScheme.surface) {
                Column(Modifier.fillMaxSize().imePadding()) {
                    ScreenHeader(stringResource(R.string.deployments_new_revision), subtitle = baseline.name, onBack = if (!busy) onDismiss else null,
                        modifier = Modifier.padding(Spacing.lg))
                    RefreshProgressIndicator(visible = busy || archiveStaging)
                    Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                        Text(stringResource(R.string.deployments_revision_effect))
                        Text(stringResource(R.string.deployments_source, stringResource(deploymentLabel(baseline.sourceKind))))
                        snapshot.revisions.firstOrNull { it.isCurrent }?.let { Text(stringResource(R.string.deployments_revision_from, it.imageReference)) }
                        if (!supported) Text(stringResource(R.string.deployments_revision_unsupported), color = MaterialTheme.colorScheme.error)

                        if (snapshot.activeOperation != null) Text(stringResource(R.string.deployments_definition_busy), color = MaterialTheme.colorScheme.error)
                        when (val outcome = result) {
                            null -> Unit
                            is ApiResult.Success -> Text(stringResource(R.string.deployments_queued, outcome.value.operationId), color = MaterialTheme.colorScheme.primary)
                            else -> OperationMessageDialog(outcome.deploymentFailure().text() + if (unknown) "\n\n${stringResource(R.string.deployments_revision_unknown)}" else "", eventKey = outcome)
                        }
                        if (unknown || result != null && result !is ApiResult.Success || snapshot.activeOperation != null)
                            TextButton(onClick = { reload(onClearArchive) }, enabled = !busy) { Text(stringResource(R.string.deployments_revision_read_current)) }
                        if (!preview) {
                            if (baseline.sourceKind == "image") {
                                RevisionText(image, { image = it }, R.string.deployments_image_reference, editable)
                                Text(stringResource(R.string.deployments_image_version_note), style = MaterialTheme.typography.bodySmall)
                            } else {
                                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                                    OutlinedButton(onClick = { pickerLauncher.launch(arrayOf("application/zip", "application/java-archive", "application/octet-stream")) }, enabled = editable && !archiveStaging) {
                                        Text(stringResource(R.string.deployments_choose_archive))
                                    }
                                    OutlinedButton(onClick = { picker = true }, enabled = editable && !archiveStaging) { Text(stringResource(R.string.deployments_choose_server_archive)) }
                                    if (archiveStaging) TextButton(onClick = onClearArchive) { Text(stringResource(R.string.common_cancel)) }
                                }
                                archive?.let { Text(it.fileName) }
                                if (expired) Text(stringResource(R.string.deployments_revision_archive_expired), color = MaterialTheme.colorScheme.error)
                                if (stagedArchive != null && stagedArchive !is ApiResult.Success) OperationMessageDialog(stagedArchive.deploymentFailure().text(), eventKey = stagedArchive)
                                RevisionText(baseImage, { baseImage = it }, R.string.deployments_base_image, editable)
                                template?.defaultBaseImage?.let { Text(stringResource(R.string.deployments_revision_default_base, it), style = MaterialTheme.typography.bodySmall) }
                                RevisionText(runtime, { runtime = it }, R.string.deployments_runtime_version, editable)
                                RevisionText(entry, { entry = it }, R.string.deployments_program_entry, editable)
                                if (template?.supportsSelfContained == true) FilterChip(selfContained, { selfContained = !selfContained }, enabled = editable,
                                    label = { Text(stringResource(R.string.deployments_self_contained)) })
                            }
                            Text(stringResource(R.string.deployments_revision_arguments), style = MaterialTheme.typography.labelLarge)
                            arguments.toList().forEachIndexed { index, argument ->
                                OutlinedTextField(argument, { arguments[index] = it }, enabled = editable, label = { Text(stringResource(R.string.deployments_revision_argument, index + 1)) }, modifier = Modifier.fillMaxWidth())
                                TextButton(onClick = { arguments.removeAt(index) }, enabled = editable) { ActionLabel(R.string.common_delete) }
                            }
                            TextButton(onClick = { arguments.add("") }, enabled = editable && arguments.size < 64) { Text(stringResource(R.string.deployments_revision_add_argument)) }
                        } else {
                            Text(if (baseline.sourceKind == "image") image.trim() else archive?.fileName.orEmpty(), style = MaterialTheme.typography.titleMedium)
                            source.baseImage?.let { Text(it) }; source.runtimeVersion?.let { Text(it) }; source.programEntry?.let { Text(it) }
                            arguments.forEachIndexed { index, argument -> Text("${index + 1}: $argument") }
                            Text(stringResource(R.string.deployments_ports, baseline.hostPort?.let { "${baseline.bindAddress}:$it" } ?: stringResource(R.string.deployments_unpublished), baseline.containerPort))
                            Text(stringResource(R.string.deployments_workload, stringResource(deploymentLabel(baseline.workloadKind))))
                            Text(stringResource(R.string.deployments_readiness, stringResource(deploymentLabel(baseline.readinessLevel))))
                            baseline.healthCheckPath?.let { Text(it) }
                            Text("${stringResource(R.string.deployments_cpu)}: ${baseline.limits.cpuCores ?: "—"}")
                            Text("${stringResource(R.string.deployments_memory_bytes)}: ${baseline.limits.memoryBytes ?: "—"}")
                            Text("${stringResource(R.string.deployments_pids)}: ${baseline.limits.pidsLimit ?: "—"}")
                            baseline.volumes.forEach { Text("${it.name} · ${it.containerPath}${if (it.readOnly) " · ro" else ""}") }
                            baseline.configuration.forEach { config ->
                                Text("${config.name}=${config.value.orEmpty()}")
                            }
                            baseline.siteId?.let { Text("${stringResource(R.string.deployments_site)}: $it") }
                            baseline.catalogTemplateId?.let { Text(stringResource(R.string.catalog_instance_version, it, baseline.catalogTemplateVersion.orEmpty())) }
                            if (selfContained) Text(stringResource(R.string.deployments_self_contained))
                            Text(stringResource(R.string.deployments_replacement_note), color = MaterialTheme.colorScheme.error)
                        }
                        Spacer(Modifier.height(Spacing.lg))
                    }
                    HorizontalDivider()
                    FlowRow(Modifier.fillMaxWidth().padding(Spacing.lg), horizontalArrangement = Arrangement.End, verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                        TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(R.string.common_close)) }
                        if (preview) TextButton(onClick = { preview = false }, enabled = editable) { Text(stringResource(R.string.common_edit)) }
                        Button(onClick = {
                            if (!preview) { preview = true; return@Button }
                            // Expiry is checked at the actual click, rather than just at composition time.
                            if (archive?.expiresAtMillis?.let { it <= System.currentTimeMillis() } == true) { preview = false; return@Button }
                            submit(source, onClearArchive, onAccepted)
    }, enabled = editable && supported && source.validFor(baseline.sourceKind) && !expired && !archiveStaging) {
                            Text(stringResource(if (preview) R.string.deployments_revision_submit else R.string.deployments_step_preview))
                        }
                    }
                }
            }
        }
        if (picker) ServerArchivePicker(onDismiss = { picker = false }, onSelect = { path -> onClearArchive(); onServerArchiveStage(path); picker = false })
    }
}

@Composable
private fun RevisionText(value: String, changed: (String) -> Unit, label: Int, enabled: Boolean) {
    OutlinedTextField(value, changed, enabled = enabled, label = { Text(stringResource(label)) }, modifier = Modifier.fillMaxWidth())
}
