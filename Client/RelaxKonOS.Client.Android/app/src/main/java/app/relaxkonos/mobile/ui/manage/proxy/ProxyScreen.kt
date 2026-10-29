package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.runtime.saveable.rememberSaveable
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.currentStateAsState
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.components.ComponentDependencyAction
import app.relaxkonos.mobile.ui.manage.operations.*
import app.relaxkonos.mobile.ui.nav.Routes
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

private class ProxyEditRequest(val kind: String, val profile: ProxyProfile?) { var open = true }
@OptIn(ExperimentalLayoutApi::class)
@Composable fun ProxyScreen(onBack: () -> Unit, initialOperationId: String? = null, modifier: Modifier = Modifier,
    onOpenComponents: () -> Unit = {}) {
    val container = appContainer()
    val model: ProxyViewModel = viewModel(factory = viewModelFactory {
        initializer { ProxyViewModel(container.session, container.proxy, container.installations,
            container.operationIndex, container.elevationAnswers) }
    })
    ProxyContent(model, container.activeSession, onBack, modifier, initialOperationId, onOpenComponents)
}

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun ProxyContent(model: ProxyViewModel, owner: SessionState.Active?, onBack: () -> Unit,
    modifier: Modifier = Modifier, initialOperationId: String? = null, onOpenComponents: () -> Unit = {}) {
    val state = model.state
    val interactionVersion = model.interactionVersion
    val adoption = model.adoptionReview?.takeIf { it.owner === owner }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val lifecycleState by lifecycle.currentStateAsState()
    val resumed = lifecycleState.isAtLeast(Lifecycle.State.RESUMED)
    val available = owner?.capabilities?.contains(ServerCapabilities.PROXY) == true
    val canManage = available && owner?.privilegedOperations == true
    val epoch = model.sessionEpoch
    var section by rememberSaveable(owner, epoch) { mutableStateOf("overview") }
    val confirm = model.changeReview?.takeIf { it.owner === owner }
    val requestChange: (() -> Unit) -> Unit = { action ->
        owner?.let { model.requestChange(interactionVersion, it, state, action) }
    }
    val cancellation = model.installationCancellation?.takeIf { it.owner === owner }
    var install by remember(owner, epoch) { mutableStateOf<Any?>(null) }
    var editor by remember(owner, epoch) { mutableStateOf<ProxyEditRequest?>(null) }
    var selected by remember(owner, epoch) { mutableStateOf<String?>(null) }
    val recovery = model.recoveryForm?.takeIf { it.owner === owner }
    val recovering = recovery != null
    var attemptedInitial by remember(owner, epoch, initialOperationId) { mutableStateOf(false) }
    val networkSection = section in setOf("connections", "logs", "settings")
    var networkEditing by remember(owner, epoch) { mutableStateOf(false) }
    var showRecovery by remember(owner, epoch) { mutableStateOf(false) }
    DisposableEffect(owner, epoch, section, lifecycle) {
        model.setInteractionActive(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED))
        val adoptionObserver = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) model.setInteractionActive(true)
            if (event == Lifecycle.Event.ON_PAUSE) model.setInteractionActive(false)
        }
        val observer = LifecycleEventObserver { _, event -> if (event == Lifecycle.Event.ON_PAUSE) {
            model.installationCancellation?.let(model::dismissInstallationCancellation); model.stopDiagnostics()
        } }
        lifecycle.addObserver(observer)
        lifecycle.addObserver(adoptionObserver)
        onDispose { model.leavePage(); lifecycle.removeObserver(adoptionObserver); lifecycle.removeObserver(observer); model.installationCancellation?.let(model::dismissInstallationCancellation); model.stopDiagnostics() }
    }
    DisposableEffect(owner, epoch, section, resumed, networkEditing, editor, install, confirm, cancellation, adoption, recovering) {
        onDispose { model.stopDiagnostics() }
    }
    OperationMessageDialog(if (state.busy || editor != null || install != null || networkEditing) null else state.problemCode?.let { proxyProblemLabel(it) } ?: if (state.uncertain) stringResource(R.string.mihomo_uncertain) else null,
        tone = if (state.problemCode == null) StatusTone.Warning else StatusTone.Danger, eventKey = owner to state.requestVersion)
    LaunchedEffect(owner, epoch) { if (available) model.refresh() }
    LaunchedEffect(owner, epoch, initialOperationId, state.busy) {
        if (available && initialOperationId != null && !state.busy && !attemptedInitial) { attemptedInitial = true; section = "records"; model.recoverOperation(initialOperationId) }
    }
    LaunchedEffect(owner, epoch, state.operation, state.installation, state.busy, state.requestVersion, state.operationVerified, state.installationVerified, editor, install, networkEditing, resumed, confirm, cancellation, adoption, state.uncertain, state.problemCode, recovering) {
        if (resumed && !recovering && editor == null && install == null && !networkEditing && confirm == null && cancellation == null && adoption == null) model.observeTasks {
            lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) && editor == null && install == null && !networkEditing && confirm == null && model.installationCancellation == null && model.adoptionReview == null && model.recoveryForm == null
        }
    }
    val overview = (state.overview as? ApiResult.Success)?.value
    val notInstalled = overview?.runtime?.state == ProxyRuntimeState.NotInstalled
    val profiles = (state.profiles as? ApiResult.Success)?.value
    val subscriptions = (state.subscriptions as? ApiResult.Success)?.value
    val current = profiles?.firstOrNull { it.id == selected }
    val ready = !state.busy && !state.uncertain && overview != null && profiles != null && subscriptions != null && state.pending.isEmpty() &&
        (state.operation == null || state.operationVerified && !state.operation.state.active) &&
        (state.installation == null || state.installationVerified && !state.installation.state.active) && !state.pendingInstallation
    val openEditor: (String, ProxyProfile?) -> Unit = { kind, profile ->
        if (owner != null && model.isCurrentInteraction(interactionVersion, owner) && model.state == state && ready && editor == null) {
            if (kind == "profile" && profile == null) selected = null
            editor = ProxyEditRequest(kind, profile)
        }
    }
    val connected = overview?.controllerReachable == true
    LaunchedEffect(owner, epoch, section, connected, state.uncertain, state.problemCode, state.busy, networkEditing, resumed, editor, install, confirm, cancellation, adoption, recovering) {
        if (resumed && !recovering && !networkEditing && editor == null && install == null && confirm == null && cancellation == null && adoption == null) model.observeDiagnostics(section) {
            lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) && !networkEditing && editor == null && install == null && confirm == null && model.installationCancellation == null && model.adoptionReview == null && model.recoveryForm == null
        }
    }
    BackHandler(section == "profiles" && selected != null && editor == null && !state.busy) { selected = null }
    // Retained sections own their item spacing; hidden sections must not add root gaps.
    WorkspaceColumn(stringResource(R.string.mihomo_title), onBack, listOf(WorkspaceDestination("overview", R.string.workspace_overview), WorkspaceDestination("profiles", R.string.workspace_profiles), WorkspaceDestination("nodes", R.string.workspace_nodes), WorkspaceDestination("connections", R.string.workspace_connections), WorkspaceDestination("logs", R.string.workspace_logs), WorkspaceDestination("settings", R.string.workspace_settings), WorkspaceDestination("records", R.string.workspace_records)), section, { section = it }, modifier, stateKey = owner to epoch, contentSpacing = 0.dp, contentPadding = PaddingValues(horizontal = Spacing.lg, vertical = Spacing.sm)) {
        if (!available) { Text(stringResource(R.string.error_capability_missing)); return@WorkspaceColumn }
WorkspaceSection(section == "overview") {
        ManagementCard {
        Text(stringResource(R.string.mihomo_intro))
        PageActionRow(refresh = {
            TextButton(enabled = !state.busy, onClick = model::refresh) { ActionLabel(R.string.common_refresh) }
        }, actions = {
            ComponentDependencyAction(Routes.MANAGE_PROXY, onOpenComponents)
            key(interactionVersion, state.requestVersion) { if (canManage) OutlinedButton(enabled = !state.busy && state.installation?.state?.active != true, onClick = {
                if (owner != null && model.isCurrentInteraction(interactionVersion, owner) && model.state == state &&
                    !model.state.busy && model.state.installation?.state?.active != true && install == null && editor == null)
                    install = Any()
            }) { Text(if (notInstalled) stringResource(R.string.runtime_install_action, "Mihomo") else stringResource(R.string.mihomo_runtime_manage)) } }
        })
        RefreshProgressIndicator(visible = state.busy)

        state.observedAtMillis?.let { Text(stringResource(R.string.mihomo_observed, DateFormat.getDateTimeInstance().format(Date(it))), style = MaterialTheme.typography.bodySmall) }
        if (overview == null) Text(stringResource(R.string.mihomo_unavailable)) else {
            if (notInstalled) Text(stringResource(R.string.runtime_install_hint, "Mihomo")) else {
            ExecutionStatusChip(proxyRuntimeLabel(overview.runtime.state), overview.runtime.state.wire, task = false)
            overview.runtime.version?.let { Text(stringResource(R.string.tunnels_version_value, it)) }
            overview.runtime.previousVersion?.let { Text(stringResource(R.string.tunnels_previous_version, it)) }
            Text(stringResource(if (overview.runtime.integrityVerified) R.string.tunnels_integrity_verified else R.string.tunnels_integrity_unverified))
            overview.runtime.problemCode.takeIf(String::isNotBlank)?.let { Text(proxyProblemLabel(it)) }
            overview.problemCode.takeIf(String::isNotBlank)?.let { Text(proxyProblemLabel(it)) }
            if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                ProxyAction.entries.filter { it in setOf(ProxyAction.Start, ProxyAction.Stop, ProxyAction.Restart) }.forEach { action ->
                    val supported = overview.runtime.state in setOf(ProxyRuntimeState.Stopped, ProxyRuntimeState.Running, ProxyRuntimeState.Degraded, ProxyRuntimeState.Failed)
                    key(interactionVersion, state.requestVersion, action) { OutlinedButton(enabled = ready && supported, onClick = { requestChange { model.queue(action) } }) { Text(proxyActionLabel(action)) } }
                }
            }
            }
        }
        }
}
        if (section != "records" && (state.pending.isNotEmpty() || state.pendingInstallation || state.uncertain || state.operation?.state?.active == true || state.installation?.state?.active == true)) TextButton(modifier = Modifier.padding(vertical = Spacing.sm), onClick = { section = "records" }) { Text(stringResource(R.string.workspace_records_attention)) }
        WorkspaceSection(section == "records") {
        if (state.operation == null && state.installation == null && state.pending.isEmpty() && !state.pendingInstallation) ManagementCard { Text(stringResource(R.string.workspace_records_empty)) }
                state.operation?.let { operation ->
            ManagementCard {
            Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
            ExecutionStatusChip(if (state.operationVerified) proxyOperationLabel(operation.state) else stringResource(R.string.mihomo_unverified), if (state.operationVerified) operation.state.wire else null)
            if (state.operationVerified) { Text(proxyStageLabel(operation.stage)); operation.problemCode.takeIf(String::isNotBlank)?.let { Text(proxyProblemLabel(it)) } }
            PageActionRow(refresh = {
                TextButton(enabled = !state.busy, onClick = model::pollOperation) { ActionLabel(R.string.common_refresh) }
            })
            }
        }
        if (notInstalled) TextButton(onClick = { showRecovery = !showRecovery }) { Text(stringResource(R.string.runtime_recovery_tools)) }
        if (!notInstalled || showRecovery) key(interactionVersion, state.requestVersion) {
            TextButton(enabled = !state.busy, onClick = { owner?.let { model.requestRecovery(interactionVersion, it, state, false) } }) { Text(stringResource(R.string.mihomo_recover_operation)) }
        }
        state.installation?.let { operation ->
            ManagementCard {
            Text(operation.operationId, style = MaterialTheme.typography.bodySmall)
            if (state.installationVerified) {
                Text(installationKindLabel(operation.kind) + " · " + installationStateLabel(operation.state)); Text(installationStageLabel(operation.stage))
                operation.progress?.let { Text(stringResource(R.string.installation_stage_progress, it)) }
                operation.problemCode?.takeIf(String::isNotBlank)?.let { Text(proxyProblemLabel(it)) }
            } else Text(stringResource(R.string.mihomo_unverified))
            PageActionRow(refresh = {
                TextButton(enabled = !state.busy, onClick = model::pollInstall) { ActionLabel(R.string.common_refresh) }
            })
            if (canManage && state.installationVerified && operation.state.active && operation.cancellable) key(interactionVersion, state.requestVersion, operation) { TextButton(enabled = !state.busy,
                onClick = { model.requestInstallationCancellation(interactionVersion, requireNotNull(owner), operation) }) { Text(stringResource(R.string.common_cancel)) } }
            }
        }
        if (state.pendingInstallation) {
            Text(stringResource(R.string.mihomo_install_pending), color = MaterialTheme.colorScheme.error)
            Text(stringResource(R.string.mihomo_install_accept_note), style = MaterialTheme.typography.bodySmall)
            if (canManage) key(interactionVersion, state.requestVersion) { TextButton(enabled = !state.busy, onClick = { requestChange(model::acceptInstallationFacts) }) {
                Text(stringResource(R.string.mihomo_accept_facts))
            } }
            key(interactionVersion, state.requestVersion) { if (canManage && model.hasIntent) TextButton(enabled = !state.busy, onClick = { requestChange(model::retryInstall) }) { ActionLabel(R.string.common_retry) } }
        }
        if (canManage && (!notInstalled || showRecovery)) key(interactionVersion, state.requestVersion) {
            TextButton(enabled = !state.busy, onClick = { owner?.let { model.requestRecovery(interactionVersion, it, state, true) } }) { Text(stringResource(R.string.mihomo_recover_installation)) }
        }
        state.pending.forEach { pending ->
            ManagementCard {
            Text(stringResource(R.string.mihomo_pending), color = MaterialTheme.colorScheme.error)
            Text(pending.key, style = MaterialTheme.typography.bodySmall)
            if (pending.action != null) {
                Text(proxyActionLabel(pending.action))
                if (canManage) key(interactionVersion, state.requestVersion, pending) { TextButton(enabled = !state.busy, onClick = { requestChange { model.resume(pending) } }) { Text(stringResource(R.string.mihomo_replay)) } }
            } else {
                Text(stringResource(R.string.mihomo_pending_sync))
                key(interactionVersion, state.requestVersion, pending) {
                    TextButton(enabled = !state.busy, onClick = {
                        model.requestAdoption(interactionVersion, requireNotNull(owner), pending)
                    }) { Text(stringResource(R.string.mihomo_accept_facts)) }
                }
            }
            }
        }
        }
        WorkspaceSection(networkSection) {
            if (!notInstalled || networkEditing) key(owner, epoch) { ProxyNetworkPanel(model, owner, canManage, ready, section,
                onEditorChanged = { networkEditing = it }) { action -> requestChange(action) } }
        }
WorkspaceSection(section == "profiles") {
        Text(stringResource(R.string.mihomo_subscriptions), style = MaterialTheme.typography.titleMedium)
        if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            key(interactionVersion, state.requestVersion) { OutlinedButton(enabled = ready, onClick = { openEditor("subscription", null) }) { Text(stringResource(R.string.mihomo_import)) } }
            key(interactionVersion, state.requestVersion) { TextButton(enabled = ready && !subscriptions.isNullOrEmpty(), onClick = { requestChange { model.queue(ProxyAction.RefreshAll) } }) { Text(stringResource(R.string.mihomo_refresh_all)) } }
        }
        if (subscriptions == null) Text(stringResource(R.string.mihomo_unavailable)) else subscriptions.forEach { subscription ->
            ManagementCard {
            Text(subscription.name + if (subscription.active) " · " + stringResource(R.string.mihomo_active) else "")
            subscription.lastUpdatedAtMillis?.let { Text(DateFormat.getDateTimeInstance().format(Date(it)), style = MaterialTheme.typography.bodySmall) }
            if (canManage) PageActionRow(refresh = {
                    key(interactionVersion, state.requestVersion, subscription) { TextButton(enabled = ready, onClick = { requestChange { model.queue(ProxyAction.RefreshSubscription, subscription.id) } }) { ActionLabel(R.string.common_refresh) } }
            }, actions = {
                key(interactionVersion, state.requestVersion, subscription) { TextButton(enabled = ready, onClick = { requestChange { model.queue(ProxyAction.ActivateSubscription, subscription.id) } }) { Text(stringResource(R.string.mihomo_activate)) } }
            })
            }
        }
        HorizontalDivider()
        Text(stringResource(R.string.mihomo_profiles), style = MaterialTheme.typography.titleMedium)
        if (canManage) key(interactionVersion, state.requestVersion) { OutlinedButton(enabled = ready, onClick = { openEditor("profile", null) }) { Text(stringResource(R.string.mihomo_create_profile)) } }
        if (profiles == null) Text(stringResource(R.string.mihomo_unavailable)) else BoxWithProfileList(profiles, selected, { selected = it }) {
            current?.let { profile ->
                ManagementCard {
                Text(profile.name, style = MaterialTheme.typography.titleSmall)
                Text(stringResource(R.string.mihomo_revision, profile.revision))
                if (canManage) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    key(interactionVersion, state.requestVersion) { TextButton(enabled = ready, onClick = { openEditor("profile", current) }) { Text(stringResource(R.string.tunnels_edit)) } }
                    key(interactionVersion, state.requestVersion, profile) { TextButton(enabled = ready && !profile.active, onClick = { requestChange { model.activate(profile) } }) { Text(stringResource(R.string.mihomo_activate)) } }
                    key(interactionVersion, state.requestVersion) { TextButton(enabled = ready && overview?.supportsValidation == true, onClick = { openEditor("yaml", current) }) { Text(stringResource(R.string.mihomo_apply_yaml)) } }
                    key(interactionVersion, state.requestVersion, profile) { TextButton(enabled = ready && !profile.active, onClick = { requestChange { model.delete(profile) } }) { ActionLabel(R.string.common_delete) } }
                }
                }
            }
        }
}
        WorkspaceSection(section == "nodes") {
        if (!notInstalled) {
        if (!connected) Text(stringResource(R.string.mihomo_controller_unavailable)) else {
            val routing = (state.routing as? ApiResult.Success)?.value
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) { ProxyRoutingMode.entries.forEach { mode ->
                key(interactionVersion, state.requestVersion, mode) { FilterChip(selected = routing == mode, enabled = canManage && ready && routing != null,
                    onClick = { requestChange { model.routing(mode) } }, label = { Text(proxyRoutingLabel(mode)) }) }
            } }
            key(owner, epoch) { ProxyNodesPanel(state, canManage, ready, model::refresh, model::select, model::testGroup) }
        }
        }
}
            }
    install?.let { request -> key(owner, epoch, request) {
        val current = { install === request && owner != null && model.isCurrentInteraction(interactionVersion, owner) }
        ProxyInstallEditor(model, interactionVersion, current, onSubmitted = { if (current()) section = "records" }) {
            if (current()) install = null
        }
    } }
    editor?.let { request -> key(owner, epoch, request) {
        DisposableEffect(request) { onDispose { request.open = false } }
        val eligible = { request.open && editor === request && owner != null &&
            model.isCurrentInteraction(interactionVersion, owner) && !model.state.busy }
        ProxyEditor(request.kind, request.profile, state, interactionVersion, eligible, { name, content, route ->
            if (eligible()) when (request.kind) {
                "profile" -> model.saveProfile(request.profile, name)
                "yaml" -> model.apply(requireNotNull(request.profile), content)
                else -> model.import(content, name, route)
            }
        }) { if (eligible()) { request.open = false; editor = null } }
    } }
    adoption?.let { review -> key(review) {
        AlertDialog(onDismissRequest = { model.dismissAdoption(review) }, title = { Text(stringResource(R.string.tunnels_confirm)) },
            text = { Text(stringResource(R.string.mihomo_change_confirm)) },
            confirmButton = { Button(enabled = !state.busy && resumed, onClick = { model.accept(review) }) { Text(stringResource(R.string.tunnels_confirm)) } },
            dismissButton = { TextButton(onClick = { model.dismissAdoption(review) }) { Text(stringResource(R.string.common_cancel)) } })
    } }
    confirm?.let { request -> key(request) { AlertDialog(onDismissRequest = { model.dismissChange(request) }, title = { Text(stringResource(R.string.tunnels_confirm)) },
        text = { Text(stringResource(R.string.mihomo_change_confirm)) }, confirmButton = { Button(enabled = !state.busy && resumed, onClick = { model.submitChange(request) }) { Text(stringResource(R.string.tunnels_confirm)) } },
        dismissButton = { TextButton(onClick = { model.dismissChange(request) }) { Text(stringResource(R.string.common_cancel)) } }) } }
    cancellation?.let { review -> key(review) { AlertDialog(onDismissRequest = { model.dismissInstallationCancellation(review) },
        title = { Text(stringResource(R.string.tunnels_confirm)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState())) {
            Text(review.operation.operationId)
            Text(stringResource(R.string.operations_cancel_explanation))
        } },
        confirmButton = { Button(enabled = !state.busy && resumed, onClick = { model.cancelInstall(review) }) { Text(stringResource(R.string.tunnels_confirm)) } },
        dismissButton = { TextButton(onClick = { model.dismissInstallationCancellation(review) }) { Text(stringResource(R.string.common_cancel)) } }) } }
    recovery?.let { form -> key(form) {
        val draft = form.draft
        AlertDialog(onDismissRequest = { model.dismissRecovery(form, interactionVersion) }, modifier = Modifier.imePadding(),
            title = { Text(stringResource(if (form.installation) R.string.mihomo_recover_installation else R.string.mihomo_recover_operation)) },
            text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.mihomo_recover_note))
                key(interactionVersion) {
                    OutlinedTextField(draft.id, { model.changeRecoveryId(form, interactionVersion, it) },
                        enabled = !state.busy && resumed, singleLine = true, label = { Text(stringResource(R.string.certificates_operation_id)) })
                    if (form.pending != null) key(draft.id) { ProxyCheck(draft.identified, !state.busy && resumed, R.string.mihomo_identify) {
                        model.identifyRecovery(form, interactionVersion, draft, it)
                    } }
                }
            } }, confirmButton = { key(interactionVersion, draft) {
                Button(enabled = !state.busy && resumed && runCatching { InstallationRoutes.canonicalId(draft.id.trim()) }.isSuccess &&
                    (form.pending == null || draft.identified), onClick = { model.submitRecovery(form, interactionVersion, draft) }) { ActionLabel(R.string.common_refresh) }
            } }, dismissButton = { key(interactionVersion) {
                TextButton(onClick = { model.dismissRecovery(form, interactionVersion) }) { Text(stringResource(R.string.common_cancel)) }
            } })
    } }

}

@Composable private fun BoxWithProfileList(profiles: List<ProxyProfile>, selected: String?, select: (String?) -> Unit, detail: @Composable () -> Unit) {
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val list: @Composable () -> Unit = { Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) { profiles.forEach { profile -> OutlinedCard(onClick = { select(profile.id) }, modifier = Modifier.fillMaxWidth()) { Column(Modifier.padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(profile.name, style = MaterialTheme.typography.titleMedium)
            if (profile.active) Text(stringResource(R.string.mihomo_active), color = MaterialTheme.colorScheme.primary)
            Text(stringResource(R.string.mihomo_revision, profile.revision), style = MaterialTheme.typography.bodySmall)
        } } } } }
        if (maxWidth >= 600.dp) Row { Column(Modifier.weight(1f)) { list() }; Column(Modifier.weight(2f)) { detail() } }
        else Column { if (selected == null) list() else { TextButton(onClick = { select(null) }) { Text(stringResource(R.string.common_back)) }; detail() } }
    }
}

private data class ProxyEditorDraft(val name: String, val content: String = "",
    val route: ProxyDownloadRoute = ProxyDownloadRoute.Direct, val confirmed: Boolean = false) {
    fun valid(kind: String, downloadOptions: Boolean): Boolean = name.length <= 128 && when (kind) {
        "profile" -> name.isNotBlank()
        "yaml" -> confirmed && content.isNotBlank() && content.length <= 1048576
        "subscription" -> confirmed && content.length <= 16384 && (route == ProxyDownloadRoute.Direct || downloadOptions) &&
            runCatching { java.net.URI(content.trim()).let { it.scheme in setOf("https", "http") && it.host != null && it.userInfo == null } }.getOrDefault(false)
        else -> false
    }
}

@Composable internal fun ProxyEditor(kind: String, profile: ProxyProfile?, state: ProxyState, actionKey: Long,
    isCurrent: () -> Boolean, submit: (String, String, ProxyDownloadRoute) -> Unit, dismiss: () -> Unit) {
    val originalName = remember { if (kind == "profile") profile?.name.orEmpty() else "" }
    var draft by remember { mutableStateOf(ProxyEditorDraft(originalName)) }
    var discard by remember { mutableStateOf<Any?>(null) }
    var finished by remember { mutableStateOf(false) }
    var consumed by remember { mutableStateOf<Any?>(null) }
    val initial = remember { state.savedEpoch }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val lifecycleState by lifecycle.currentStateAsState()
    val latestState by rememberUpdatedState(state)
    val latestKey by rememberUpdatedState(actionKey)
    val latestCurrent by rememberUpdatedState(isCurrent)
    val latestDismiss by rememberUpdatedState(dismiss)
    val snapshot = draft
    val ticket = remember(actionKey, draft, state.requestVersion, state.busy, state.pending, state.savedEpoch) { Any() }
    val currentTicket by rememberUpdatedState(ticket)
    fun eligible() = !finished && latestKey == actionKey && latestCurrent() && !latestState.busy &&
        lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
    fun finish() { if (eligible()) { finished = true; discard = null; latestDismiss() } }
    DisposableEffect(Unit) { onDispose { finished = true } }
    LaunchedEffect(actionKey) { discard = null }
    LaunchedEffect(state.savedEpoch, state.busy, actionKey) { if (state.savedEpoch != initial) finish() }
    val close = {
        if (eligible() && discard == null) {
            if (draft != ProxyEditorDraft(originalName)) discard = Any() else finish()
        }
    }
    val enabled = !state.busy && !finished && discard == null && lifecycleState.isAtLeast(Lifecycle.State.RESUMED)
    AlertDialog(onDismissRequest = close, modifier = Modifier.imePadding(), title = { Text(stringResource(when (kind) { "yaml" -> R.string.mihomo_apply_yaml; "subscription" -> R.string.mihomo_import; else -> R.string.mihomo_profiles })) },
        text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            key(actionKey) {
            if (kind != "yaml") ProxyText(draft.name, enabled, R.string.mihomo_name) { if (eligible() && discard == null) draft = draft.copy(name = it) }
            if (kind == "yaml") { Text(stringResource(R.string.mihomo_yaml_note)); OutlinedTextField(draft.content, { if (eligible() && discard == null && it.length <= 1048576) draft = draft.copy(content = it) }, enabled = enabled, minLines = 6, label = { Text(stringResource(R.string.mihomo_yaml)) }) }
            if (kind == "subscription") {
                Text(stringResource(R.string.mihomo_subscription_note))
                OutlinedTextField(draft.content, { if (eligible() && discard == null) draft = draft.copy(content = it) }, enabled = enabled, singleLine = true, visualTransformation = PasswordVisualTransformation(), label = { Text(stringResource(R.string.mihomo_source_url)) })
                ProxyDownloadRoute.entries.forEach { option -> RadioOption(draft.route == option, stringResource(if (option == ProxyDownloadRoute.Direct) R.string.mihomo_download_direct else R.string.mihomo_download_system), enabled && (option == ProxyDownloadRoute.Direct || state.downloadOptions)) {
                    if (eligible() && discard == null && (option == ProxyDownloadRoute.Direct || latestState.downloadOptions)) draft = draft.copy(route = option)
                } }
            }
            if (kind != "profile") ProxyCheck(draft.confirmed, enabled, R.string.mihomo_apply_confirm) { if (eligible() && discard == null) draft = draft.copy(confirmed = it) }
            }
            ProxyEditorFeedback(state)
        } }, confirmButton = { key(ticket) { Button(enabled = enabled && state.savedEpoch == initial && state.pending.isEmpty() && draft.valid(kind, state.downloadOptions), onClick = {
            if (eligible() && discard == null && currentTicket === ticket && consumed !== ticket && draft === snapshot &&
                latestState.savedEpoch == initial && latestState.pending.isEmpty() && snapshot.valid(kind, latestState.downloadOptions)) {
                consumed = ticket; submit(snapshot.name, snapshot.content, snapshot.route)
            }
        }) { ActionLabel(R.string.common_save) } } },
        dismissButton = { key(actionKey) { TextButton(enabled = enabled, onClick = close) { Text(stringResource(R.string.common_cancel)) } } })
    discard?.let { decision -> key(decision) {
        AlertDialog(onDismissRequest = { if (eligible() && discard === decision) discard = null }, text = { Text(stringResource(R.string.mihomo_discard)) },
            confirmButton = { TextButton(onClick = { if (eligible() && discard === decision) finish() }) { Text(stringResource(R.string.common_close)) } },
            dismissButton = { TextButton(onClick = { if (eligible() && discard === decision) discard = null }) { Text(stringResource(R.string.common_cancel)) } })
    } }
}
@Composable internal fun ProxyEditorFeedback(state: ProxyState) {
    if (!state.busy) {
        state.problemCode?.let { Text(proxyProblemLabel(it), color = MaterialTheme.colorScheme.error) }
        if (state.uncertain || state.pending.isNotEmpty()) Text(stringResource(R.string.mihomo_uncertain), color = MaterialTheme.colorScheme.error)
    }
    RefreshProgressIndicator(visible = state.busy)
}
@Composable internal fun ProxyText(value: String, enabled: Boolean, label: Int, change: (String) -> Unit) { OutlinedTextField(value, change, enabled = enabled, singleLine = true, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(label)) }) }
@Composable internal fun ProxyCheck(value: Boolean, enabled: Boolean, label: Int, change: (Boolean) -> Unit) { CheckboxOption(value, stringResource(label), enabled, change) }
