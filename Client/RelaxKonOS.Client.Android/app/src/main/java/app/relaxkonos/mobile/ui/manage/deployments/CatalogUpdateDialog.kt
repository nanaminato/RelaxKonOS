package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.ui.common.RefreshProgressIndicator

import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.UUID

@Composable
internal fun CatalogUpdateDialog(owner: SessionState.Active, initial: DeploymentApplication, templates: List<CatalogTemplate>,
    runtime: DeploymentRuntime?, onDismiss: () -> Unit, onAccepted: (DeploymentOperation) -> Unit) {
    val container = (LocalContext.current.applicationContext as RelaxKonApplication).container
    val scope = rememberCoroutineScope()
    var baseline by remember { mutableStateOf(initial) }
    val targets = templates.filter { it.id == baseline.catalogTemplateId }
    var selectedVersion by remember { mutableStateOf(targets.firstOrNull()?.version) }
    val target = targets.firstOrNull { it.version == selectedVersion }
    var preview by remember { mutableStateOf<ApiResult<CatalogApplicationUpdatePreview>?>(null) }
    var result by remember { mutableStateOf<ApiResult<DeploymentOperation>?>(null) }
    var busy by remember { mutableStateOf(false) }
    var unknown by remember { mutableStateOf(container.deployments.hasUncertainRevision(owner, baseline.id)) }
    var reload by remember { mutableIntStateOf(0) }
    LaunchedEffect(selectedVersion, baseline, reload) {
        preview = null
        if (target != null) preview = withContext(Dispatchers.IO) { container.deployments.previewCatalogUpdate(owner, baseline.id, target.version) }
    }
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
                            baseline.configuration.forEach { config -> Text(if (config.isSecret) config.name + " · " + stringResource(R.string.deployments_secret_version, config.secretVersion ?: 0) else "${config.name}=${config.value.orEmpty()}") }
                            baseline.siteId?.let { Text("${stringResource(R.string.deployments_site)}: $it") }
                            Text(stringResource(R.string.deployments_replacement_note), color = MaterialTheme.colorScheme.error)
                        }
                        null -> Unit
                        else -> OperationMessageDialog(loaded.deploymentFailure().text(), eventKey = loaded, tone = loaded.deploymentFailure().tone)
                    }

                    when (val outcome = result) {
                        is ApiResult.Success -> Text(stringResource(R.string.deployments_queued, outcome.value.operationId), color = MaterialTheme.colorScheme.primary)
                        null -> Unit
                        else -> OperationMessageDialog(outcome.deploymentFailure().text() + if (unknown) "\n\n${stringResource(R.string.deployments_revision_unknown)}" else "", eventKey = outcome,
                            tone = if (unknown) app.relaxkonos.mobile.ui.common.StatusTone.Warning else outcome.deploymentFailure().tone)
                    }
                    if (result !is ApiResult.Success) TextButton(onClick = {
                        busy = true
                        scope.launch {
                            try {
                                when (val current = withContext(Dispatchers.IO) { container.deployments.reconcileRevision(owner, baseline.id) }) {
                                    is ApiResult.Success -> {
                                        if (current.value.application.id != baseline.id) { result = ApiResult.Transport(null); return@launch }
                                        baseline = current.value.application; unknown = container.deployments.hasUncertainRevision(owner, baseline.id); result = null; reload++
                                    }
                                    is ApiResult.Problem -> result = current
                                    is ApiResult.Transport -> result = current
                                }
                            } finally { busy = false }
                        }
                    }, enabled = !busy) { Text(stringResource(R.string.deployments_revision_read_current)) }
                    Spacer(Modifier.height(Spacing.lg))
                }
                HorizontalDivider()
                FlowRow(Modifier.fillMaxWidth().padding(Spacing.lg), horizontalArrangement = Arrangement.End, verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                    TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(R.string.common_close)) }
                    Button(onClick = {
                        val confirmed = diff ?: return@Button
                        busy = true; result = null
                        scope.launch {
                            try {
                                val submitted = withContext(Dispatchers.IO) { container.deployments.updateCatalog(owner, baseline, confirmed, UUID.randomUUID().toString()) }
                                if (container.session.state.value !== owner) return@launch
                                result = submitted.result; unknown = submitted.mayHaveQueued
                                if (submitted.result is ApiResult.Success) onAccepted(submitted.result.value)
                            } finally { busy = false }
                        }
                    }, enabled = !busy && !unknown && result !is ApiResult.Success && exact && diff?.blockers?.isEmpty() == true && compatibility?.canInstall == true) {
                        Text(stringResource(R.string.catalog_update_confirm, selectedVersion.orEmpty()))
                    }
                }
            }
        }
    }
}
