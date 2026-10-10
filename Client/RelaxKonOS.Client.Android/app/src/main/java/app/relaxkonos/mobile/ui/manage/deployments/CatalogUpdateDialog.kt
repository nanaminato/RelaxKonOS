package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.ui.common.RefreshProgressIndicator

import app.relaxkonos.mobile.ui.common.OperationMessageDialog
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
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.theme.Spacing

@Composable
internal fun CatalogUpdateDialog(owner: SessionState.Active, initial: DeploymentApplication, templates: List<CatalogTemplate>,
    runtime: DeploymentRuntime?, onDismiss: () -> Unit, onAccepted: (DeploymentOperation) -> Unit) {
    val container = appContainer()
    val scope = rememberCoroutineScope()
    val editor = remember(DeploymentOwnerKey(owner), initial.id, templates, scope) { CatalogUpdateEditor(container.session, container.deployments, owner, initial, templates, scope) }
    DisposableEffect(editor) { onDispose { editor.close() } }
    CatalogUpdateContent(editor, owner, runtime, onDismiss, onAccepted)
}

@Composable
internal fun CatalogUpdateContent(editor: CatalogUpdateEditor, owner: SessionState.Active, runtime: DeploymentRuntime?,
    onDismiss: () -> Unit, onAccepted: (DeploymentOperation) -> Unit) {
    with(editor) {
        LaunchedEffect(editor, selectedVersion, baseline, reload) { refreshPreview() }
        val diff = (preview as? ApiResult.Success)?.value
        val exact = diff != null && diff.applicationId == baseline.id && diff.expectedUpdatedAt == baseline.updatedAt &&
            diff.currentTemplateVersion == baseline.catalogTemplateVersion && diff.target == target
        val compatibility = target?.compatibility(owner.capabilities, runtime)
        Dialog(onDismissRequest = { if (!busy) onDismiss() }, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
            Surface(Modifier.fillMaxSize().safeDrawingPadding(), color = MaterialTheme.colorScheme.surface) {
                Column(Modifier.fillMaxSize().imePadding()) {
                    ScreenHeader(stringResource(R.string.catalog_update_title), subtitle = baseline.name, onBack = if (!busy) onDismiss else null, modifier = Modifier.padding(Spacing.lg))
                    RefreshProgressIndicator(visible = busy || target != null && preview == null)
                    Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                        Text(stringResource(R.string.catalog_update_note))
                        Text(stringResource(R.string.catalog_instance_version, baseline.catalogTemplateId.orEmpty(), baseline.catalogTemplateVersion.orEmpty()))
                        if (targets.isEmpty()) Text(stringResource(R.string.catalog_update_unavailable), color = MaterialTheme.colorScheme.error)
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                            targets.forEach { choice -> FilterChip(choice.version == selectedVersion, { selectedVersion = choice.version; result = null }, enabled = !busy && !unknown && result !is ApiResult.Success,
                                label = { Text(choice.version) }) }
                        }
                        target?.let {
                            Text(it.description)
                            Text(stringResource(R.string.catalog_source, it.publisher, it.source), style = MaterialTheme.typography.bodySmall)
                            compatibility?.blockers?.forEach { blocker -> Text(catalogBlockerText(blocker), color = MaterialTheme.colorScheme.error) }
                        }
                        when (val loaded = preview) {
                            is ApiResult.Success -> {
                                val change = loaded.value
                                if (!exact) Text(stringResource(R.string.deployments_definition_conflict), color = MaterialTheme.colorScheme.error)
                                Text(stringResource(R.string.catalog_update_versions, change.currentTemplateVersion, change.target.version), style = MaterialTheme.typography.titleMedium)
                                Text(stringResource(R.string.catalog_update_image_before, change.currentImageReference ?: "—"))
                                Text(stringResource(R.string.catalog_update_image_after, change.targetImageReference))
                                Text(change.updateNotes)
                                Text(change.target.maintenanceNotes, style = MaterialTheme.typography.bodySmall)
                                change.blockers.forEach { problem -> Text(deploymentProblem(problem).text(), color = MaterialTheme.colorScheme.error) }
                                Text(stringResource(R.string.catalog_update_retained), style = MaterialTheme.typography.titleMedium)
                                Text(stringResource(R.string.deployments_ports, baseline.hostPort?.let { "${baseline.bindAddress}:$it" } ?: stringResource(R.string.deployments_unpublished), baseline.containerPort))
                                Text("${stringResource(R.string.deployments_memory_bytes)}: ${baseline.limits.memoryBytes ?: "—"}")
                                Text("${stringResource(R.string.deployments_cpu)}: ${baseline.limits.cpuCores ?: "—"}")
                                Text("${stringResource(R.string.deployments_pids)}: ${baseline.limits.pidsLimit ?: "—"}")
                                Text(stringResource(R.string.deployments_readiness, stringResource(deploymentLabel(baseline.readinessLevel))))
                                baseline.healthCheckPath?.let { Text(it) }
                                baseline.volumes.forEach { Text("${it.name} · ${it.containerPath}${if (it.readOnly) " · ro" else ""}") }
                                baseline.configuration.forEach { config -> Text("${config.name}=${config.value.orEmpty()}") }
                                baseline.siteId?.let { Text("${stringResource(R.string.deployments_site)}: $it") }
                                Text(stringResource(R.string.deployments_replacement_note), color = MaterialTheme.colorScheme.error)
                            }
                            null -> Unit
                            else -> OperationMessageDialog(loaded.deploymentFailure().text(), eventKey = previewVersion, tone = loaded.deploymentFailure().tone)
                        }

                        when (val outcome = result) {
                            is ApiResult.Success -> Text(stringResource(R.string.deployments_queued, outcome.value.operationId), color = MaterialTheme.colorScheme.primary)
                            null -> Unit
                            else -> OperationMessageDialog(outcome.deploymentFailure().text() + if (unknown) "\n\n${stringResource(R.string.deployments_revision_unknown)}" else "", eventKey = feedbackVersion,
                                tone = if (unknown) app.relaxkonos.mobile.ui.common.StatusTone.Warning else outcome.deploymentFailure().tone)
                        }
                        if (result !is ApiResult.Success) TextButton(onClick = { readCurrent() }, enabled = !busy) { Text(stringResource(R.string.deployments_revision_read_current)) }
                        Spacer(Modifier.height(Spacing.lg))
                    }
                    HorizontalDivider()
                    FlowRow(Modifier.fillMaxWidth().padding(Spacing.lg), horizontalArrangement = Arrangement.End, verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                        TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(R.string.common_close)) }
                        Button(onClick = { submit(diff ?: return@Button, onAccepted) }, enabled = !busy && !unknown && result !is ApiResult.Success && exact && diff?.blockers?.isEmpty() == true && compatibility?.canInstall == true) {
                            Text(stringResource(R.string.catalog_update_confirm, selectedVersion.orEmpty()))
                        }
                    }
                }
            }
        }
    }
}
