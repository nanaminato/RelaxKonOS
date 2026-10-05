package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.ui.common.RefreshProgressIndicator

import app.relaxkonos.mobile.ui.common.ActionLabel
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
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

/** A complete definition edit uses the original login and never queues a deployment. */
@Composable
internal fun DeploymentDefinitionDialog(owner: SessionState.Active, baseline: DeploymentApplication, onDismiss: () -> Unit, onSaved: () -> Unit) {
    val container = appContainer()
    val scope = rememberCoroutineScope()
    val editor = remember(DeploymentOwnerKey(owner), baseline.id, scope) { DeploymentDefinitionEditor(container, owner, baseline, scope) }
    DisposableEffect(editor) { onDispose { editor.close() } }
    with(editor) {
        val request = editor.request
        Dialog(onDismissRequest = { if (!busy) onDismiss() }, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
            Surface(Modifier.fillMaxSize().safeDrawingPadding(), color = MaterialTheme.colorScheme.surface) {
                Column(Modifier.fillMaxSize().imePadding()) {
                    ScreenHeader(stringResource(R.string.deployments_edit_definition), subtitle = draft.baseline.name, onBack = if (!busy) onDismiss else null,
                        modifier = Modifier.padding(Spacing.lg))
                    RefreshProgressIndicator(visible = busy)
                    Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                        Text(stringResource(R.string.deployments_definition_effect))
                        Text(stringResource(R.string.deployments_source, stringResource(deploymentLabel(baseline.sourceKind))), style = MaterialTheme.typography.bodySmall)
                        draft.baseline.catalogTemplateId?.let { Text(stringResource(R.string.catalog_instance_version, it, draft.baseline.catalogTemplateVersion.orEmpty())) }
                        if (loadedCurrent) Text(stringResource(R.string.deployments_definition_loaded), color = MaterialTheme.colorScheme.primary)
                        if (pending) Text(stringResource(R.string.deployments_definition_busy), color = MaterialTheme.colorScheme.error)

                        when (val outcome = result) {
                            is ApiResult.Success -> Text(stringResource(R.string.deployments_definition_saved, outcome.value.name), color = MaterialTheme.colorScheme.primary)
                            null -> Unit
                            else -> OperationMessageDialog(outcome.deploymentFailure().text() + if (unknown) "\n\n${stringResource(R.string.deployments_definition_unknown)}" else "", eventKey = outcome)
                        }
                        if (result != null && !saved || pending) TextButton(onClick = ::loadCurrent, enabled = !busy) { Text(stringResource(R.string.deployments_definition_load_current)) }
                        if (!preview) {
                            DefinitionText(draft.name, { draft.name = it }, R.string.deployments_name, editable)
                            Text(stringResource(R.string.deployments_name_rule), style = MaterialTheme.typography.bodySmall)
                            DefinitionOptions(listOf("web", "worker"), draft.workload, editable) { draft.workload = it; if (it == "worker") draft.readiness = "process" }
                            DefinitionOptions(if (draft.workload == "web") listOf("http", "process") else listOf("process"), draft.readiness, editable) { draft.readiness = it }
                            DefinitionText(draft.containerPort, { draft.containerPort = it }, R.string.deployments_container_port, editable, KeyboardType.Number)
                            DefinitionText(draft.hostPort, { draft.hostPort = it }, R.string.deployments_host_port, editable, KeyboardType.Number)
                            Text(stringResource(R.string.deployments_host_port_note), style = MaterialTheme.typography.bodySmall)
                            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                                listOf("127.0.0.1", "0.0.0.0", "::1").forEach { address -> FilterChip(selected = draft.bindAddress == address,
                                    onClick = { draft.bindAddress = address }, enabled = editable, label = { Text(address) }) }
                            }
                            if (draft.readiness == "http") DefinitionText(draft.healthPath, { draft.healthPath = it }, R.string.deployments_health_path, editable)
                            DefinitionText(draft.cpuCores, { draft.cpuCores = it }, R.string.deployments_cpu, editable, KeyboardType.Decimal)
                            DefinitionText(draft.memoryBytes, { draft.memoryBytes = it }, R.string.deployments_memory_bytes, editable, KeyboardType.Number)
                            DefinitionText(draft.pidsLimit, { draft.pidsLimit = it }, R.string.deployments_pids, editable, KeyboardType.Number)
                            Text(stringResource(R.string.deployments_volumes), style = MaterialTheme.typography.titleMedium)
                            draft.volumes.toList().forEach { volume ->
                                Text("${volume.name} · ${volume.containerPath}${if (volume.readOnly) " · ro" else ""}")
                                FlowRow {
                                    TextButton(onClick = { volumeName = volume.name; volumePath = volume.containerPath; volumeReadOnly = volume.readOnly }, enabled = editable) { Text(stringResource(R.string.common_edit)) }
                                    TextButton(onClick = { draft.volumes.remove(volume) }, enabled = editable) { ActionLabel(R.string.common_delete) }
                                }
                            }
                            DefinitionText(volumeName, { volumeName = it }, R.string.deployments_volume_name, editable)
                            DefinitionText(volumePath, { volumePath = it }, R.string.deployments_volume_path, editable)
                            FlowRow {
                                FilterChip(volumeReadOnly, { volumeReadOnly = !volumeReadOnly }, enabled = editable, label = { Text(stringResource(R.string.deployments_volume_read_only)) })
                                TextButton(onClick = {
                                    entryInvalid = !draft.putVolume(volumeName, volumePath, volumeReadOnly)
                                    if (!entryInvalid) { volumeName = ""; volumePath = ""; volumeReadOnly = false }
                                }, enabled = editable) { Text(stringResource(R.string.deployments_configuration_add)) }
                            }
                            Text(stringResource(R.string.deployments_configuration), style = MaterialTheme.typography.titleMedium)
                            Text(stringResource(R.string.deployments_secret_retention), style = MaterialTheme.typography.bodySmall)
                            draft.configuration.toList().forEach { config ->
                                Text(config.name)
                                Text(config.value.orEmpty(), style = MaterialTheme.typography.bodySmall)
                                FlowRow {
                                    TextButton(onClick = { configName = config.name; configValue = config.value.orEmpty(); configSecret = config.isSecret }, enabled = editable) { Text(stringResource(R.string.common_edit)) }
                                    TextButton(onClick = { draft.configuration.remove(config) }, enabled = editable) { ActionLabel(R.string.common_delete) }
                                }
                            }
                            DefinitionText(configName, { configName = it }, R.string.deployments_configuration_name, editable)
                            OutlinedTextField(configValue, { configValue = it.take(4096) }, enabled = editable, label = { Text(stringResource(R.string.deployments_configuration_value)) },
                                visualTransformation = VisualTransformation.None, modifier = Modifier.fillMaxWidth())
                            FlowRow {
                                FilterChip(configSecret, { configSecret = !configSecret; configValue = "" }, enabled = editable, label = { Text(stringResource(R.string.deployments_configuration_secret)) })
                                TextButton(onClick = {
                                    entryInvalid = !draft.putConfiguration(configName, configValue, configSecret)
                                    if (!entryInvalid) { configName = ""; configValue = ""; configSecret = false }
                                }, enabled = editable) { Text(stringResource(R.string.deployments_configuration_add)) }
                            }
                            DefinitionText(draft.siteId, { draft.siteId = it }, R.string.deployments_site, editable)
                            Text(stringResource(R.string.deployments_site_note), style = MaterialTheme.typography.bodySmall)
                            if (entryInvalid || request == null) Text(stringResource(R.string.deployments_definition_invalid), color = MaterialTheme.colorScheme.error)
                            if (unstaged) Text(stringResource(R.string.deployments_definition_unstaged), style = MaterialTheme.typography.bodySmall)
                        } else if (request != null) {
                            Text(request.name, style = MaterialTheme.typography.titleMedium)
                            Text(stringResource(R.string.deployments_ports, request.hostPort?.let { "${request.bindAddress}:$it" } ?: stringResource(R.string.deployments_unpublished), request.containerPort))
                            Text(stringResource(R.string.deployments_workload, stringResource(deploymentLabel(request.workloadKind))))
                            Text(stringResource(R.string.deployments_readiness, stringResource(deploymentLabel(request.readinessLevel))))
                            request.healthCheckPath?.let { Text(it) }
                            Text("${stringResource(R.string.deployments_cpu)}: ${request.limits.cpuCores ?: "—"}")
                            Text("${stringResource(R.string.deployments_memory_bytes)}: ${request.limits.memoryBytes ?: "—"}")
                            Text("${stringResource(R.string.deployments_pids)}: ${request.limits.pidsLimit ?: "—"}")
                            Text(stringResource(R.string.deployments_preview_counts, request.volumes.size, request.configuration.count { !it.isSecret }, request.configuration.count { it.isSecret }))
                            request.volumes.forEach { Text("${it.name} · ${it.containerPath}${if (it.readOnly) " · ro" else ""}") }
                            request.configuration.forEach { Text(it.name) }
                            Text("${stringResource(R.string.deployments_site)}: ${request.siteId ?: "—"}")
                        }
                        Spacer(Modifier.height(Spacing.lg))
                    }
                    HorizontalDivider()
                    FlowRow(Modifier.fillMaxWidth().padding(Spacing.lg), horizontalArrangement = Arrangement.End, verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                        TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(if (saved) R.string.common_close else R.string.common_cancel)) }
                        if (preview) TextButton(onClick = { preview = false }, enabled = editable) { Text(stringResource(R.string.common_edit)) }
                        if (!preview) Button(onClick = { preview = true }, enabled = editable && request != null && !unstaged) { Text(stringResource(R.string.deployments_step_preview)) }
                        else Button(onClick = { save(onSaved) }, enabled = editable && request != null && !unstaged) { Text(stringResource(R.string.deployments_save_definition)) }
                    }
                }
            }
        }
    }
}

@Composable
private fun DefinitionText(value: String, onChange: (String) -> Unit, label: Int, enabled: Boolean, keyboard: KeyboardType = KeyboardType.Text) {
    OutlinedTextField(value, { onChange(it.take(256)) }, enabled = enabled, label = { Text(stringResource(label)) },
        keyboardOptions = KeyboardOptions(keyboardType = keyboard), modifier = Modifier.fillMaxWidth())
}

@Composable
private fun DefinitionOptions(values: List<String>, selected: String, enabled: Boolean, onSelect: (String) -> Unit) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        values.forEach { value -> FilterChip(selected == value, { onSelect(value) }, enabled = enabled, label = { Text(stringResource(deploymentLabel(value))) }) }
    }
}
