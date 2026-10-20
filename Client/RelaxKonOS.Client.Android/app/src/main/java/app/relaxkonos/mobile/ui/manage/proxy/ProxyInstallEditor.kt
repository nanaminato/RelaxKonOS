package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.operations.installationKindLabel
import app.relaxkonos.mobile.ui.theme.Spacing
import org.json.JSONObject

@Composable internal fun ProxyInstallEditor(model: ProxyViewModel, onSubmitted: () -> Unit, dismiss: () -> Unit) {
    val state = model.state
    var source by remember { mutableStateOf<String?>(null) }
    var release by remember { mutableStateOf<String?>(null) }
    var kind by remember { mutableStateOf(InstallationKind.Install) }
    var prerelease by remember { mutableStateOf(false) }
    var keepData by remember { mutableStateOf(true) }
    var confirmed by remember { mutableStateOf(false) }
    var closeConfirmed by remember(state.runtimeRecovery) { mutableStateOf(false) }
    val locked = state.busy || model.hasIntent
    val runtime = state.runtime
    val catalog = state.runtimeCatalog
    LaunchedEffect(Unit) { model.loadRuntime() }
    LaunchedEffect(runtime?.ownership) { if (runtime?.ownership == "managed") kind = InstallationKind.Repair }
    val initialId = remember { state.installation?.operationId }
    LaunchedEffect(state.installation?.operationId) {
        if (state.installation != null && state.installation.operationId != initialId) { onSubmitted(); dismiss() }
    }
    fun changed() { confirmed = false; model.clearRuntimePlan() }
    DraftCloseGuard(!state.busy, source != null || release != null || !keepData || confirmed || prerelease, dismiss) { requestClose ->
    AlertDialog(onDismissRequest = requestClose, modifier = Modifier.imePadding(),
        title = { Text(stringResource(R.string.mihomo_runtime_manage)) },
        text = { Column(Modifier.heightIn(max = 500.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            TextButton(enabled = !locked, onClick = { changed(); model.loadRuntime() }) { Text(stringResource(R.string.common_refresh)) }
            if (runtime == null) Text(stringResource(R.string.managed_runtime_unknown))
            else {
                Text(stringResource(R.string.managed_runtime_identity, runtime.ownership, runtime.version.orEmpty()))
                runtime.problem?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
            if (runtime != null && !runtime.recoveryRequired && runtime.problem == null && runtime.ownership in setOf("absent", "managed")) {
                val kinds = if (runtime.ownership == "absent") listOf(InstallationKind.Install)
                    else listOf(InstallationKind.Upgrade, InstallationKind.Repair, InstallationKind.Rollback, InstallationKind.Uninstall)
                kinds.forEach { option -> RadioOption(kind == option, installationKindLabel(option), !locked) {
                    kind = option; release = if (option == InstallationKind.Repair) runtime.activeRelease else null; changed()
                } }
                if (kind == InstallationKind.Uninstall) {
                    RadioOption(keepData, stringResource(R.string.managed_runtime_keep_data), !locked) { keepData = true; changed() }
                    RadioOption(!keepData, stringResource(R.string.managed_runtime_delete_data), !locked) { keepData = false; changed() }
                } else {
                    Text(stringResource(R.string.managed_runtime_sources))
                    if (state.runtimeSources.isEmpty()) Text(stringResource(R.string.managed_runtime_no_sources))
                    state.runtimeSources.forEach { item ->
                        RadioOption(source == item.id, "${item.publisher} · ${item.id} · ${item.trust}\n${item.address}\n${item.fingerprint}", !locked && item.enabled && item.trust == "confirmed") {
                            source = item.id; release = if (kind == InstallationKind.Repair) runtime.activeRelease else null; changed()
                            model.loadRuntimeCatalog(item.id, false, prerelease)
                        }
                    }
                    Row { Checkbox(prerelease, enabled = !locked, onCheckedChange = {
                        prerelease = it; changed(); source?.let { id -> model.loadRuntimeCatalog(id, false, it) }
                    }); Text(stringResource(R.string.managed_runtime_prerelease)) }
                    TextButton(enabled = !locked && source != null, onClick = {
                        changed(); source?.let { model.loadRuntimeCatalog(it, true, prerelease) }
                    }) { Text(stringResource(R.string.mihomo_releases_refresh)) }
                    if (catalog != null) {
                        if (!catalog.fresh || catalog.problem != null) Text(stringResource(R.string.managed_runtime_stale), color = MaterialTheme.colorScheme.error)
                        catalog.problem?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                        catalog.releases.filter { kind != InstallationKind.Rollback || it.id in runtime.storedReleases && it.id != runtime.activeRelease }.forEach { item ->
                            RadioOption(release == item.id, "${item.version} · ${item.target} · ${item.trust} · ${item.evidence}",
                                !locked && catalog.fresh && catalog.problem == null && item.trust == "confirmed" && item.supported &&
                                    (prerelease || item.channel == "stable") && runtime.eligible(kind, item.id)) { release = item.id; changed() }
                            if (!item.supported) Text(stringResource(R.string.managed_runtime_unsupported))
                        }
                    }
                }
                TextButton(enabled = !locked && !state.pendingInstallation && (kind == InstallationKind.Uninstall || release != null),
                    onClick = { confirmed = false; model.planRuntime(kind, release, prerelease, keepData) }) {
                    Text(stringResource(R.string.managed_runtime_preview))
                }
            }
            state.runtimeDependencies?.let { report ->
                val observations = JSONObject(report).getJSONArray("observations")
                for (index in 0 until observations.length()) {
                    val item = observations.getJSONObject(index)
                    Text("${item.getString("kind")} · ${item.getString("identity")} · ${item.getString("state")}")
                    if (!item.isNull("requiredBytes")) Text(stringResource(R.string.managed_runtime_disk,
                        if (item.isNull("availableBytes")) "?" else item.getLong("availableBytes").toString(), item.getLong("requiredBytes").toString()))
                }
            }
            state.runtimeProblems.forEach { Text(it, color = MaterialTheme.colorScheme.error) }
            state.runtimePlan?.let { plan ->
                val json = JSONObject(plan.plan)
                Text(stringResource(R.string.managed_runtime_plan, installationKindLabel(plan.kind),
                    json.optNullableString("previousReleaseId").orEmpty(),
                    if (json.isNull("release")) "" else json.getJSONObject("release").getString("originalVersion")))
                Text(stringResource(R.string.managed_runtime_restart))
                Row { Checkbox(confirmed, enabled = !state.busy, onCheckedChange = { confirmed = it }); Text(stringResource(R.string.mihomo_install_confirm)) }
            }
            state.runtimeRecovery?.let { payload ->
                val recovery = JSONObject(payload)
                if (recovery.getBoolean("required")) {
                    Text(stringResource(R.string.managed_runtime_recovery), color = MaterialTheme.colorScheme.error)
                    val id = recovery.optNullableString("operationId")
                    id?.let { Text(it) }
                    recovery.optNullableString("problemCode")?.let { Text(it) }
                    recovery.optNullableString("recoveryProblemCode")?.let { Text(it) }
                    if (id != null) {
                        TextButton(enabled = !locked, onClick = { model.recoverRuntime("retry", id, false) }) { Text(stringResource(R.string.common_retry)) }
                        TextButton(enabled = !locked, onClick = { model.recoverRuntime("stop", id, false) }) { Text(stringResource(R.string.managed_runtime_stop)) }
                        val service = if (recovery.isNull("service")) null else recovery.getJSONObject("service")
                        if (!recovery.isNull("recoveryProblemCode") && service != null && !service.getBoolean("running") && recovery.isNull("serviceProblem")) {
                            Row { Checkbox(closeConfirmed, enabled = !locked, onCheckedChange = { closeConfirmed = it }); Text(stringResource(R.string.managed_runtime_close_confirm)) }
                            TextButton(enabled = !locked && closeConfirmed, onClick = { closeConfirmed = false; model.recoverRuntime("close", id, true) }) { Text(stringResource(R.string.managed_runtime_close)) }
                        }
                    }
                }
            }
            ProxyEditorFeedback(state)
        } },
        confirmButton = { Button(enabled = !state.busy && confirmed && (model.hasIntent || state.runtimePlan != null) && state.installation?.state?.active != true,
            onClick = { model.installRuntime() }) { Text(stringResource(if (model.hasIntent) R.string.common_retry else R.string.tunnels_confirm)) } },
        dismissButton = { TextButton(enabled = !state.busy, onClick = requestClose) { Text(stringResource(R.string.common_close)) } })
    }
}
