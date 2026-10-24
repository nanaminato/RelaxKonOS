package app.relaxkonos.mobile.ui.manage.proxyserver

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.currentStateAsState
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.operations.*
import kotlinx.coroutines.delay
import org.json.JSONObject

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun SingBoxRuntimePanel() {
    val container = appContainer()
    val model: SingBoxRuntimeViewModel = viewModel(factory = viewModelFactory { initializer {
        SingBoxRuntimeViewModel(container.session, container.proxyServer, container.installations, container.elevationAnswers)
    } })
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val resumed = lifecycle.currentStateAsState().value.isAtLeast(Lifecycle.State.RESUMED)
    val owner = container.activeSession
    val visible = owner != null && model.owner === owner
    val canManage = visible && resumed && owner?.privilegedOperations == true
    val host = model.installation.takeIf { visible }
    val catalog = model.catalog.takeIf { visible }
    val plan = model.plan.takeIf { visible }
    val operation = model.operation.takeIf { visible }
    var source by remember(owner) { mutableStateOf<String?>(null) }
    var release by remember(owner) { mutableStateOf<String?>(null) }
    var kind by remember(owner) { mutableStateOf(InstallationKind.Install) }
    var prerelease by remember(owner) { mutableStateOf(false) }
    var keepData by remember(owner) { mutableStateOf(true) }
    var reviewed by remember(owner) { mutableStateOf<ManagedRuntimeInstallationRequest?>(null) }
    var decision by remember(owner) { mutableStateOf<(() -> Unit)?>(null) }
    val locked = !canManage || model.busy || model.pending || model.hasIntent || operation?.state?.active == true
    LaunchedEffect(owner, model.owner, resumed) { if (visible && resumed) model.refresh() }
    DisposableEffect(owner, lifecycle) {
        val observer = LifecycleEventObserver { _, event -> if (event == Lifecycle.Event.ON_PAUSE) { reviewed = null; decision = null; model.stop() } }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); model.stop() }
    }
    LaunchedEffect(owner, operation, model.busy, resumed) {
        if (visible && resumed && operation?.state?.active == true && !model.busy) { delay(1500); model.observe() }
    }
    LaunchedEffect(host?.ownership, host?.activeRelease) {
        if (!model.hasIntent) { kind = if (host?.ownership == "absent") InstallationKind.Install else InstallationKind.Upgrade; release = null; reviewed = null }
    }
    Text(stringResource(R.string.proxy_server_runtime_title), style = MaterialTheme.typography.titleMedium)
    TextButton(enabled = visible && !model.busy, onClick = { reviewed = null; model.refresh() }) { Text(stringResource(R.string.common_refresh)) }
    RefreshProgressIndicator(visible && model.busy)
    if (host == null) Text(stringResource(R.string.managed_runtime_unknown))
    else {
        Text(stringResource(R.string.managed_runtime_identity, managedRuntimeOwnershipLabel(host.ownership), host.version ?: stringResource(R.string.managed_runtime_version_unmeasured)))
        host.problem?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        if (host.recoveryRequired) Text(stringResource(R.string.managed_runtime_recovery))
        if (host.ownership in setOf("absent", "managed") && !host.recoveryRequired) {
            val kinds = if (host.ownership == "absent") listOf(InstallationKind.Install) else listOf(InstallationKind.Upgrade, InstallationKind.Repair, InstallationKind.Rollback, InstallationKind.Uninstall)
            kinds.forEach { choice -> RadioOption(choice == kind, installationKindLabel(choice), !locked) {
                kind = choice; release = if (choice == InstallationKind.Repair) host.activeRelease else null; reviewed = null; model.clearPlan()
            } }
            if (kind == InstallationKind.Uninstall) {
                CheckboxOption(keepData, stringResource(R.string.managed_runtime_keep_data), !locked) { keepData = it; reviewed = null; model.clearPlan() }
                if (!keepData) Text(stringResource(R.string.managed_runtime_delete_data), color = MaterialTheme.colorScheme.error)
            } else {
                Text(stringResource(R.string.managed_runtime_sources))
                if (model.sources.isEmpty()) Text(stringResource(R.string.managed_runtime_no_sources))
                model.sources.forEach { item -> RadioOption(item.id == source,
                    "${item.publisher} · ${item.id}\n${managedRuntimeTrustLabel(item.trust)}\n${item.address}\n${item.fingerprint}", !locked && item.trusted) {
                    source = item.id; release = null; reviewed = null; model.catalog(item.id, false, prerelease)
                } }
                CheckboxOption(prerelease, stringResource(R.string.managed_runtime_prerelease), !locked) {
                    prerelease = it; release = null; reviewed = null; model.clearPlan()
                    source?.let { id -> model.catalog(id, false, prerelease) }
                }
                TextButton(enabled = !locked && source != null, onClick = { source?.let { reviewed = null; release = null; model.catalog(it, true, prerelease) } }) { Text(stringResource(R.string.common_refresh)) }
                if (catalog != null) {
                    if (!catalog.usable) Text(stringResource(R.string.managed_runtime_stale), color = MaterialTheme.colorScheme.error)
                    catalog.problem?.let { Text(it) }
                    catalog.releases.forEach { item ->
                        RadioOption(release == item.id, "${item.version} · ${item.target} · ${managedRuntimeTrustLabel(item.trust)} · ${managedRuntimeEvidenceLabel(item.evidence)}",
                            !locked && catalog.usable && item.supported && item.trust == "confirmed" && (prerelease || item.channel == "stable") && host.eligible(kind, item.id)) {
                            release = item.id; reviewed = null; model.clearPlan()
                        }
                        if (!item.supported) Text(stringResource(R.string.managed_runtime_unsupported))
                    }
                }
            }
            OutlinedButton(enabled = !locked && host.problem == null && (kind == InstallationKind.Uninstall || catalog?.usable == true && release != null), onClick = {
                reviewed = null; model.propose(kind, release, prerelease, keepData)
            }) { Text(stringResource(R.string.managed_runtime_preview)) }
        }
    }
    model.dependencies?.takeIf { visible }?.let { payload ->
        val items = JSONObject(payload).getJSONArray("items")
        for (i in 0 until items.length()) {
            val item = items.getJSONObject(i)
            Text(stringResource(R.string.managed_runtime_dependency_observation, managedRuntimeDependencyKindLabel(item.getString("kind")), item.getString("identity"), managedRuntimeDependencyStateLabel(item.getString("state"))))
            if (!item.isNull("requiredBytes")) Text(stringResource(R.string.managed_runtime_disk, if (item.isNull("availableBytes")) stringResource(R.string.managed_resource_unknown) else item.getLong("availableBytes").toString(), item.getLong("requiredBytes").toString()))
        }
    }
    if (visible) model.problems.forEach { Text(it, color = MaterialTheme.colorScheme.error) }
    plan?.let { request -> key(request) {
        val json = JSONObject(request.plan)
        Text(stringResource(R.string.managed_runtime_plan, installationKindLabel(request.kind), json.optNullableString("previousReleaseId") ?: stringResource(R.string.managed_runtime_no_release),
            if (json.isNull("release")) stringResource(R.string.managed_runtime_no_release) else json.getJSONObject("release").getString("originalVersion")))
        Text(stringResource(R.string.managed_runtime_restart))
        CheckboxOption(reviewed === request, stringResource(R.string.proxy_server_runtime_confirm), canManage && !model.busy) { reviewed = if (it) request else null }
        Button(enabled = canManage && !model.busy && reviewed === request && operation?.state?.active != true, onClick = { if (reviewed === request && model.plan === request) model.submit(request) }) {
            Text(stringResource(if (model.hasIntent) R.string.common_retry else R.string.proxy_server_confirm_title))
        }
    } }
    if (visible && model.pending) {
        Text(stringResource(R.string.managed_runtime_pending_intent))
        if (!model.hasIntent) TextButton(enabled = canManage && !model.busy, onClick = { decision = model::acceptFacts }) { Text(stringResource(R.string.proxy_server_runtime_accept_facts)) }
    }
    operation?.let { current ->
        Text(current.operationId)
        Text(installationKindLabel(current.kind) + " · " + installationStateLabel(current.state) + " · " + installationStageLabel(current.stage))
        current.problemCode?.let { Text(it) }
        current.progress?.let { LinearProgressIndicator(progress = { it / 100f }, modifier = Modifier.fillMaxWidth()) }
        if (model.operationVerified && current.state.active && current.cancellable) TextButton(enabled = canManage && !model.busy, onClick = { decision = { model.cancel(current) } }) { Text(stringResource(R.string.common_cancel)) }
    }
    model.recovery?.takeIf { visible }?.let { payload -> key(payload) {
        val json = JSONObject(payload)
        if (json.getBoolean("required")) {
            Text(stringResource(R.string.managed_runtime_recovery))
            json.optNullableString("operationId")?.let { id ->
                Text(id)
                TextButton(enabled = canManage && !model.busy, onClick = { model.recover("retry", payload, false) }) { Text(stringResource(R.string.common_retry)) }
                TextButton(enabled = canManage && !model.busy, onClick = { decision = { model.recover("stop", payload, false) } }) { Text(stringResource(R.string.managed_runtime_stop)) }
                if (!json.isNull("recoveryProblemCode") && !json.isNull("service") && !json.getJSONObject("service").getBoolean("running") && json.isNull("serviceProblem"))
                    TextButton(enabled = canManage && !model.busy, onClick = { decision = { model.recover("close", payload, true) } }) { Text(stringResource(R.string.managed_runtime_close)) }
            }
        }
    } }
    decision?.let { action -> AlertDialog(onDismissRequest = { decision = null }, text = { Text(stringResource(R.string.proxy_server_runtime_review_warning)) },
        confirmButton = { TextButton(enabled = canManage && !model.busy, onClick = { decision = null; action() }) { Text(stringResource(R.string.proxy_server_confirm_title)) } },
        dismissButton = { TextButton(onClick = { decision = null }) { Text(stringResource(R.string.common_cancel)) } }) }
}
