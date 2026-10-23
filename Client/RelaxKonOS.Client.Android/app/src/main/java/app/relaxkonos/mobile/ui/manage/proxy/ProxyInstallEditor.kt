package app.relaxkonos.mobile.ui.manage.proxy

import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.currentStateAsState
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

@Composable internal fun ProxyInstallEditor(model: ProxyViewModel, actionKey: Long, isCurrent: () -> Boolean, onSubmitted: () -> Unit, dismiss: () -> Unit) {
    val state = model.state
    var source by remember { mutableStateOf<String?>(null) }
    var release by remember { mutableStateOf<String?>(null) }
    var kind by remember { mutableStateOf(InstallationKind.Install) }
    var prerelease by remember { mutableStateOf(false) }
    var keepData by remember { mutableStateOf(true) }
    var confirmedPlan by remember { mutableStateOf<ManagedRuntimeInstallationRequest?>(null) }
    var closeConfirmed by remember(state.runtimeRecovery) { mutableStateOf(false) }
    var finished by remember { mutableStateOf(false) }
    var discard by remember { mutableStateOf<Any?>(null) }
    var consumed by remember { mutableStateOf<Any?>(null) }
    var loaded by remember { mutableStateOf(false) }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val lifecycleState by lifecycle.currentStateAsState()
    val latestKey by rememberUpdatedState(actionKey)
    val latestCurrent by rememberUpdatedState(isCurrent)
    val latestDismiss by rememberUpdatedState(dismiss)
    val latestSubmitted by rememberUpdatedState(onSubmitted)
    fun eligible() = !finished && latestKey == actionKey && latestCurrent() && !model.state.busy &&
        lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
    fun finish(submitted: Boolean = false) {
        if (eligible()) { finished = true; discard = null; if (submitted) latestSubmitted(); latestDismiss() }
    }
    DisposableEffect(Unit) { onDispose { finished = true } }
    LaunchedEffect(actionKey, state.requestVersion) { discard = null }
    val enabled = !state.busy && !finished && discard == null && lifecycleState.isAtLeast(Lifecycle.State.RESUMED)
    val locked = !enabled || model.hasIntent
    val runtime = state.runtime
    val catalog = state.runtimeCatalog
    val selectedSource = state.runtimeSources.singleOrNull { it.id == source }
    val catalogReady = catalog?.usable == true && catalog.source == selectedSource
    val canPreview = !state.pendingInstallation && runtime != null && (kind == InstallationKind.Uninstall ||
        catalogReady && release != null && runtime.eligible(kind, requireNotNull(release)) && catalog.releases.any {
            it.id == release && it.trust == "confirmed" && it.supported && (prerelease || it.channel == "stable")
        })
    LaunchedEffect(actionKey, lifecycleState) {
        if (!loaded && eligible()) { loaded = true; model.loadRuntime() }
    }
    LaunchedEffect(runtime?.ownership) {
        if (!model.hasIntent && runtime?.ownership == "managed" && kind == InstallationKind.Install)
            kind = InstallationKind.Repair
    }
    LaunchedEffect(state.runtimePlan, model.hasIntent) {
        if (model.hasIntent) state.runtimePlan?.let { plan ->
            val request = JSONObject(plan.plan).getJSONObject("request")
            kind = plan.kind; release = request.optNullableString("releaseId")
            prerelease = request.getBoolean("allowPrerelease"); keepData = request.getBoolean("keepData")
            confirmedPlan = null
        }
    }
    val initialId = remember { state.installation?.operationId }
    LaunchedEffect(state.installation?.operationId, state.busy, actionKey, lifecycleState) {
        if (state.installation != null && state.installation.operationId != initialId) finish(submitted = true)
    }
    fun changed() { confirmedPlan = null; model.clearRuntimePlan() }
    val draft = listOf(source, release, kind, prerelease, keepData)
    val control = remember(actionKey, draft, state.requestVersion, state.busy, state.runtime, state.runtimeCatalog, model.hasIntent) { Any() }
    val currentControl by rememberUpdatedState(control)
    fun fields(action: () -> Unit) {
        if (eligible() && discard == null && !model.hasIntent && currentControl === control &&
            model.state.requestVersion == state.requestVersion && listOf(source, release, kind, prerelease, keepData) == draft &&
            model.state.runtime === runtime && model.state.runtimeCatalog === catalog) action()
    }
    val requestClose = {
        if (eligible() && discard == null) {
            val defaultKind = if (runtime?.ownership == "managed") InstallationKind.Repair else InstallationKind.Install
            if (source != null || release != null || !keepData || confirmedPlan != null || prerelease || kind != defaultKind) discard = Any()
            else finish()
        }
    }
    val plan = state.runtimePlan
    val submission = remember(actionKey, state.requestVersion, plan, confirmedPlan, state.busy, state.installation) { Any() }
    val currentSubmission by rememberUpdatedState(submission)
    AlertDialog(onDismissRequest = requestClose, modifier = Modifier.imePadding(),
        title = { Text(stringResource(R.string.mihomo_runtime_manage)) },
        text = { Column(Modifier.heightIn(max = 500.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            key(control) {
            TextButton(enabled = !locked, onClick = { fields { changed(); model.loadRuntime() } }) { Text(stringResource(R.string.common_refresh)) }
            if (runtime == null) Text(stringResource(R.string.managed_runtime_unknown))
            else {
                if (runtime.ownership == "absent") Text(managedRuntimeOwnershipLabel(runtime.ownership))
                else Text(stringResource(R.string.managed_runtime_identity, managedRuntimeOwnershipLabel(runtime.ownership),
                    runtime.version?.takeIf(String::isNotBlank) ?: stringResource(R.string.managed_runtime_version_unmeasured)))
                runtime.problem?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
            if (runtime != null && !runtime.recoveryRequired && runtime.problem == null && runtime.ownership in setOf("absent", "managed")) {
                val kinds = if (runtime.ownership == "absent") listOf(InstallationKind.Install)
                    else listOf(InstallationKind.Upgrade, InstallationKind.Repair, InstallationKind.Rollback, InstallationKind.Uninstall)
                kinds.forEach { option -> RadioOption(kind == option, installationKindLabel(option), !locked) {
                    fields { kind = option; release = if (option == InstallationKind.Repair) runtime.activeRelease else null; changed() }
                } }
                if (kind == InstallationKind.Uninstall) {
                    RadioOption(keepData, stringResource(R.string.managed_runtime_keep_data), !locked) { fields { keepData = true; changed() } }
                    RadioOption(!keepData, stringResource(R.string.managed_runtime_delete_data), !locked) { fields { keepData = false; changed() } }
                } else {
                    Text(stringResource(R.string.managed_runtime_sources))
                    if (state.runtimeSources.isEmpty()) Text(stringResource(R.string.managed_runtime_no_sources))
                    state.runtimeSources.forEach { item ->
                        RadioOption(source == item.id, "${item.publisher} · ${item.id}\n${managedRuntimeTrustLabel(item.trust)}\n${item.address}\n${item.fingerprint}", !locked && item.enabled && item.trust == "confirmed") {
                            fields {
                                if (item.enabled && item.trust == "confirmed") {
                                    source = item.id; release = if (kind == InstallationKind.Repair) runtime.activeRelease else null; changed()
                                    model.loadRuntimeCatalog(item.id, false, prerelease)
                                }
                            }
                        }
                    }
                    ProxyCheck(prerelease, !locked, R.string.managed_runtime_prerelease) {
                        fields { prerelease = it; changed(); source?.let { id -> model.loadRuntimeCatalog(id, false, it) } }
                    }
                    TextButton(enabled = !locked && source != null, onClick = {
                        fields { changed(); source?.let { model.loadRuntimeCatalog(it, true, prerelease) } }
                    }) { Text(stringResource(R.string.mihomo_releases_refresh)) }
                    if (catalog != null) {
                        if (!catalog.fresh || catalog.problem != null) Text(stringResource(R.string.managed_runtime_stale), color = MaterialTheme.colorScheme.error)
                        catalog.problem?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                        if (!catalog.trusted || catalog.source != selectedSource)
                            Text(stringResource(R.string.managed_runtime_trust_changed), color = MaterialTheme.colorScheme.error)
                        catalog.releases.filter { kind != InstallationKind.Rollback || it.id in runtime.storedReleases && it.id != runtime.activeRelease }.forEach { item ->
                            RadioOption(release == item.id, "${item.version} · ${item.target} · ${managedRuntimeTrustLabel(item.trust)} · ${managedRuntimeEvidenceLabel(item.evidence)}",
                                !locked && catalogReady && item.trust == "confirmed" && item.supported &&
                                    (prerelease || item.channel == "stable") && runtime.eligible(kind, item.id)) { fields { release = item.id; changed() } }
                            if (!item.supported) Text(stringResource(R.string.managed_runtime_unsupported))
                        }
                    }
                }
                TextButton(enabled = !locked && canPreview,
                    onClick = { fields {
                        if (canPreview && !model.state.pendingInstallation) {
                            confirmedPlan = null; model.planRuntime(kind, release, prerelease, keepData)
                        }
                    } }) {
                    Text(stringResource(R.string.managed_runtime_preview))
                }
            }
            }
            state.runtimeDependencies?.let { report ->
                val observations = JSONObject(report).getJSONArray("observations")
                for (index in 0 until observations.length()) {
                    val item = observations.getJSONObject(index)
                    Text(stringResource(R.string.managed_runtime_dependency_observation,
                        managedRuntimeDependencyKindLabel(item.getString("kind")), item.getString("identity"),
                        managedRuntimeDependencyStateLabel(item.getString("state"))))
                    if (!item.isNull("requiredBytes")) Text(stringResource(R.string.managed_runtime_disk,
                        if (item.isNull("availableBytes")) stringResource(R.string.managed_resource_unknown) else item.getLong("availableBytes").toString(), item.getLong("requiredBytes").toString()))
                }
            }
            state.runtimeProblems.forEach { Text(it, color = MaterialTheme.colorScheme.error) }
            state.runtimePlan?.let { plan ->
                val json = JSONObject(plan.plan)
                Text(stringResource(R.string.managed_runtime_plan, installationKindLabel(plan.kind),
                    json.optNullableString("previousReleaseId") ?: stringResource(R.string.managed_runtime_no_release),
                    if (json.isNull("release")) stringResource(R.string.managed_runtime_no_release) else json.getJSONObject("release").getString("originalVersion")))
                Text(stringResource(R.string.managed_runtime_restart))
                key(actionKey, state.requestVersion, plan) {
                    ProxyCheck(confirmedPlan === plan, enabled, R.string.mihomo_install_confirm) {
                        if (eligible() && discard == null && model.state.runtimePlan === plan && model.state.requestVersion == state.requestVersion)
                            confirmedPlan = if (it) plan else null
                    }
                }
            }
            state.runtimeRecovery?.let { payload -> key(actionKey, state.requestVersion, payload) {
                val recovery = JSONObject(payload)
                val recoveryTicket = remember { Any() }
                fun recover(action: String, id: String, confirmation: Boolean) {
                    if (eligible() && discard == null && !model.hasIntent && model.state.runtimeRecovery == payload &&
                        model.state.requestVersion == state.requestVersion && consumed !== recoveryTicket &&
                        (action != "close" || confirmation && closeConfirmed)) {
                        consumed = recoveryTicket; closeConfirmed = false; model.recoverRuntime(action, id, confirmation)
                    }
                }
                if (recovery.getBoolean("required")) {
                    Text(stringResource(R.string.managed_runtime_recovery), color = MaterialTheme.colorScheme.error)
                    val id = recovery.optNullableString("operationId")
                    id?.let { Text(it) }
                    recovery.optNullableString("problemCode")?.let { Text(it) }
                    recovery.optNullableString("recoveryProblemCode")?.let { Text(it) }
                    if (id != null) {
                        TextButton(enabled = !locked, onClick = { recover("retry", id, false) }) { Text(stringResource(R.string.common_retry)) }
                        TextButton(enabled = !locked, onClick = { recover("stop", id, false) }) { Text(stringResource(R.string.managed_runtime_stop)) }
                        val service = if (recovery.isNull("service")) null else recovery.getJSONObject("service")
                        if (!recovery.isNull("recoveryProblemCode") && service != null && !service.getBoolean("running") && recovery.isNull("serviceProblem")) {
                            ProxyCheck(closeConfirmed, !locked, R.string.managed_runtime_close_confirm) {
                                if (eligible() && discard == null && !model.hasIntent && model.state.runtimeRecovery == payload &&
                                    model.state.requestVersion == state.requestVersion) closeConfirmed = it
                            }
                            TextButton(enabled = !locked && closeConfirmed, onClick = { recover("close", id, true) }) { Text(stringResource(R.string.managed_runtime_close)) }
                        }
                    }
                }
            }
            }
            if (model.hasIntent) Text(stringResource(R.string.managed_runtime_pending_intent), color = MaterialTheme.colorScheme.error)
            ProxyEditorFeedback(state)
        } },
        confirmButton = { key(submission) {
            Button(enabled = enabled && plan != null && confirmedPlan === plan && state.installation?.state?.active != true,
                onClick = {
                    if (eligible() && discard == null && currentSubmission === submission && consumed !== submission &&
                        plan != null && confirmedPlan === plan && model.state.runtimePlan === plan &&
                        model.state.requestVersion == state.requestVersion && model.state.installation?.state?.active != true) {
                        consumed = submission; model.installRuntime()
                    }
                }) { Text(stringResource(if (model.hasIntent) R.string.common_retry else R.string.tunnels_confirm)) }
        } }, dismissButton = { key(actionKey) {
            TextButton(enabled = enabled, onClick = requestClose) { Text(stringResource(R.string.common_close)) }
        } })
    discard?.let { decision -> key(actionKey, decision) {
        AlertDialog(onDismissRequest = { if (eligible() && discard === decision) discard = null },
            title = { Text(stringResource(R.string.ui_discard_draft_title)) },
            confirmButton = { TextButton(onClick = { if (eligible() && discard === decision) finish() }) { Text(stringResource(R.string.editor_discard_changes)) } },
            dismissButton = { TextButton(onClick = { if (eligible() && discard === decision) discard = null }) { Text(stringResource(R.string.common_cancel)) } })
    } }
}
