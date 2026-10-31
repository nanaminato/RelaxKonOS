package app.relaxkonos.mobile.ui.manage.tailscale

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.TailscaleReferencePhase
import app.relaxkonos.mobile.data.TailscaleRepository
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.CancellationException

@OptIn(ExperimentalLayoutApi::class)
@Composable fun TailscaleScreen(onBack: () -> Unit, modifier: Modifier = Modifier, onOpenComponents: () -> Unit = {}) {
    val container = appContainer()
    val model: TailscaleViewModel = viewModel(factory = viewModelFactory {
        initializer { TailscaleViewModel(container.session,container.tailscaleControl,container.elevationAnswers) }
    })
    val login: TailscaleLoginViewModel = viewModel(factory = viewModelFactory {
        initializer { TailscaleLoginViewModel(container.session,container.tailscaleLogin,container.elevationAnswers) }
    })
    val owner = container.activeSession
    val state = model.state
    val visible = state.owner === owner
    var page by rememberSaveable(owner) { mutableStateOf("devices") }
    var draft by remember(owner) { mutableStateOf<TailscalePreferences?>(null) }
    var status by remember(owner) { mutableStateOf<TailscaleStatus?>(null) }
    var statusBusy by remember(owner) { mutableStateOf(false) }
    var statusFailed by remember(owner) { mutableStateOf(false) }
    var statusRefresh by remember(owner) { mutableIntStateOf(0) }
    val repository = remember(container) { TailscaleRepository(container.gateway, container.session) }
    val uriHandler = LocalUriHandler.current
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    DisposableEffect(owner,lifecycle) {
        model.setActive(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED))
        login.setActive(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED))
        val observer = LifecycleEventObserver { _,_ ->
            val active = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
            model.setActive(active); login.setActive(active)
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); model.stop(); login.stop() }
    }
    LaunchedEffect(owner,statusRefresh,model.viewEpoch) {
        if (owner == null || ServerCapabilities.TAILSCALE !in owner.capabilities) return@LaunchedEffect
        statusBusy = true; statusFailed = false
        try {
            when (val result = repository.status(owner)) {
                is ApiResult.Success -> status = result.value
                else -> statusFailed = true
            }
        } catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { statusFailed = true }
        finally { statusBusy = false }
    }
    LaunchedEffect(owner,page) { if (owner != null && page == "login") statusRefresh++ }
    LaunchedEffect(visible,page,state.busy,state.control,state.problem) {
        if (visible && page == "control" && !state.busy && state.control == null && state.problem == null) model.loadControl()
    }
    LaunchedEffect(owner,state.owner) { if (owner != null && visible) model.loadRecords() }
    LaunchedEffect(owner,login.state.owner) { if (owner != null && login.state.owner === owner) login.loadRecords() }
    WorkspaceColumn(stringResource(R.string.tailscale_title),onBack,
        listOf(WorkspaceDestination("devices",R.string.tailscale_peers),WorkspaceDestination("control",R.string.tailscale_control),WorkspaceDestination("login",R.string.tailscale_login_title),WorkspaceDestination("records",R.string.tailscale_records)),
        page,{ if (!state.busy && !login.state.busy) { model.dismiss(); login.dismiss(); page = it } },modifier,stateKey = owner, titleActions = { app.relaxkonos.mobile.ui.manage.components.ComponentDependencyAction(
            app.relaxkonos.mobile.ui.nav.Routes.MANAGE_TAILSCALE, onOpenComponents) }) {
        if (owner == null || ServerCapabilities.TAILSCALE !in owner.capabilities) {
            Text(stringResource(R.string.error_capability_missing)); return@WorkspaceColumn
        }

        if (page == "devices") TailscaleDevicesContent(status,statusBusy,statusFailed,{ statusRefresh++ },{ model.authorizeView() })
        if (page == "login" && login.state.owner === owner) TailscaleLoginContent(login,{
            model.invalidateControl(); statusRefresh++
        },status,statusBusy,statusFailed,{ statusRefresh++ })
        if (visible && page != "devices" && page != "login") {
            Text(owner.workspaceName)
            RefreshProgressIndicator(visible = state.busy)
            if (state.problem != null) Text(stringResource(when(state.problem) {
                "tailscale.storage_failed" -> R.string.tailscale_storage_failed
                "tailscale.plan_expired" -> R.string.tailscale_plan_expired
                "tailscale.elevation_required", "tailscale.authorization_required", "elevation-required" -> R.string.tailscale_authorization_required
                else -> R.string.tailscale_unverified
            }),color = MaterialTheme.colorScheme.error)
            if (state.records.any { it.phase != TailscaleReferencePhase.Known }) Text(stringResource(R.string.tailscale_pending))
            if (page == "control") {
                Text(stringResource(R.string.tailscale_control_intro))
                if (state.control == null) {
                    TextButton(enabled = !state.busy,onClick = model::loadControl) { Text(stringResource(R.string.common_refresh)) }
                } else {
                    ControlContent(state.control,status,model.editable,state.busy,
                        onRefresh = model::loadControl,
                        onEdit = { draft = it },
                        onConnect = { model.prepare(TailscaleAction.Connect) },
                        onDisconnect = { model.prepare(TailscaleAction.Disconnect) },
                        onLogout = { model.prepare(TailscaleAction.Logout) },
                        onAdminConsole = { runCatching { uriHandler.openUri("https://login.tailscale.com/admin/machines") } })
                }
            }
            if (page == "records") RecordsContent(model,state,status)
        }
    }
    val config = draft
    val control = state.control
    if (config != null && control != null && visible) AlertDialog(onDismissRequest = { draft = null },
        title = { Text(stringResource(R.string.tailscale_preferences)) },
        text = { PreferencesEditor(config,control,status,model.editable,onAuthorize = { model.authorizeView() }) },
        confirmButton = { Button(enabled = model.editable && config != control.preferences,
            onClick = { draft = null; model.prepare(TailscaleAction.ConfigurePreferences,preferences = config) }) { Text(stringResource(R.string.tailscale_preview)) } },
        dismissButton = { TextButton(onClick = { draft = null }) { Text(stringResource(R.string.common_cancel)) } })
    val review = model.review?.takeIf { visible && it.owner === owner }
    if (review != null) ReviewDialog(review,status,onConfirm = { model.submit(review) },onDismiss = model::dismiss,busy = state.busy)
}

@OptIn(ExperimentalLayoutApi::class)
@Composable private fun ControlContent(control: TailscaleControl, status: TailscaleStatus?, editable: Boolean, busy: Boolean,
    onRefresh: () -> Unit, onEdit: (TailscalePreferences) -> Unit, onConnect: () -> Unit, onDisconnect: () -> Unit,
    onLogout: () -> Unit, onAdminConsole: () -> Unit) {
    ManagementCard {
        Text(stringResource(when {
            !control.authenticated -> R.string.tailscale_needs_login
            control.connected -> R.string.tailscale_connected
            else -> R.string.tailscale_disconnected
        }), style = MaterialTheme.typography.titleMedium)
        status?.account?.let {
            Text(it.displayName.ifBlank { it.loginName })
            if (it.loginName.isNotBlank()) Text(it.loginName)
        }
        status?.tailnetName?.let { Text(stringResource(R.string.tailscale_tailnet, it)) }
        status?.self?.let { Text(stringResource(R.string.tailscale_this_device, it.hostName, it.addresses.firstOrNull().orEmpty())) }
        PreferenceSummary(control.preferences,status,control.platform)
        if (control.preferences == null) ConfigurationText(control.configuration)
        if (!control.authenticated) Text(stringResource(R.string.tailscale_login_required_hint))
        if (!control.canControl) Text(stringResource(R.string.tailscale_control_unavailable))
    }
    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        Button(enabled = editable && control.authenticated && !control.connected,onClick = onConnect) { Text(stringResource(R.string.tailscale_connect)) }
        OutlinedButton(enabled = editable && control.connected,onClick = onDisconnect) { Text(stringResource(R.string.tailscale_disconnect)) }
        OutlinedButton(enabled = editable && control.preferences != null,onClick = { control.preferences?.let(onEdit) }) { Text(stringResource(R.string.tailscale_edit_preferences)) }
        OutlinedButton(enabled = editable && control.authenticated,onClick = onLogout) { Text(stringResource(R.string.tailscale_logout)) }
    }
    TextButton(enabled = !busy,onClick = onRefresh) { Text(stringResource(R.string.common_refresh)) }
    TextButton(onClick = onAdminConsole) { Text(stringResource(R.string.tailscale_admin_console)) }
}

@Composable private fun RecordsContent(model: TailscaleViewModel, state: TailscaleState, status: TailscaleStatus?) {
    Text(stringResource(R.string.tailscale_records_intro))
    if (state.records.isEmpty()) Text(stringResource(R.string.tailscale_records_empty))
    state.records.forEach { reference ->
        FilterChip(selected = state.selected == reference.operationId,enabled = !state.busy,
            onClick = { model.select(reference.operationId) },label = { Text(reference.operationId) })
    }
    TextButton(enabled = state.selected != null && !state.busy,onClick = model::read) { Text(stringResource(R.string.tailscale_reopen)) }
    state.operation?.let { operation ->
        ManagementCard {
            Text(actionLabel(operation.action))
            Text(stringResource(if (operation.closure != null) R.string.tailscale_closed else when(operation.state) {
                TailscaleOperationState.Prepared -> R.string.tailscale_operation_prepared
                TailscaleOperationState.Applying -> R.string.tailscale_operation_applying
                TailscaleOperationState.Applied -> R.string.tailscale_operation_applied
                TailscaleOperationState.Rejected -> R.string.tailscale_operation_rejected
                TailscaleOperationState.Unknown -> R.string.tailscale_operation_unknown
            }))
        }
        if (operation.state == TailscaleOperationState.Unknown && operation.closure == null) {
            TextButton(enabled = !state.busy,onClick = model::inspect) { Text(stringResource(R.string.tailscale_inspect)) }
        }
    }
    state.recovery?.let { recovery ->
        recovery.observation?.let { observation ->
            Text(stringResource(if (!observation.authenticated) R.string.tailscale_needs_login
                else if (observation.connected) R.string.tailscale_connected else R.string.tailscale_disconnected))
            PreferenceSummary(observation.preferences,status,observation.platform)
        }
        TextButton(enabled = !state.busy && recovery.canClose && state.owner?.privilegedOperations == true,onClick = model::requestClose) { Text(stringResource(R.string.tailscale_close)) }
    }
}

@Composable private fun PreferencesEditor(initial: TailscalePreferences, control: TailscaleControl,
    status: TailscaleStatus?, editable: Boolean, onAuthorize: () -> Unit) {
    var value by remember(initial) { mutableStateOf(initial) }
    val candidates = status?.peers?.filter { it.exitNodeOption }.orEmpty()
    val options = buildList {
        add(SelectOption("",stringResource(R.string.tailscale_exit_node_none)))
        add(SelectOption("auto:any",stringResource(R.string.tailscale_exit_node_auto)))
        candidates.forEach { peer ->
            add(SelectOption(peer.addresses.firstOrNull().orEmpty(),peer.hostName + " · " + peer.addresses.joinToString()))
        }
        val current = value.exitNode
        if (current != null && none { it.value == current }) add(SelectOption(current,current))
    }
    Column(Modifier.verticalScroll(rememberScrollState())) {
        PreferenceSwitch(stringResource(R.string.tailscale_preference_incoming),!value.shieldsUp,editable) { value = value.copy(shieldsUp = !it) }
        PreferenceSwitch(stringResource(R.string.tailscale_accept_dns),value.acceptDns,editable) { value = value.copy(acceptDns = it) }
        PreferenceSwitch(stringResource(R.string.tailscale_accept_routes),value.acceptRoutes,editable) { value = value.copy(acceptRoutes = it) }
        PreferenceSwitch(stringResource(R.string.tailscale_preference_auto_update),value.autoUpdate,editable) { value = value.copy(autoUpdate = it) }
        if (control.platform == "windows") {
            PreferenceSwitch(stringResource(R.string.tailscale_preference_unattended),value.unattended,editable) { value = value.copy(unattended = it) }
        }
        PreferenceSwitch(stringResource(R.string.tailscale_preference_advertise_exit_node),value.advertiseExitNode,editable) { value = value.copy(advertiseExitNode = it) }
        Text(stringResource(R.string.tailscale_preference_advertise_hint),style = MaterialTheme.typography.bodySmall)
        SelectField(stringResource(R.string.tailscale_exit_node_title),options,value.exitNode ?: "",
            { selected -> value = value.copy(exitNode = selected.ifEmpty { null },exitNodeAllowLanAccess = if (selected.isEmpty()) false else value.exitNodeAllowLanAccess) })
        PreferenceSwitch(stringResource(R.string.tailscale_preference_lan_access),value.exitNodeAllowLanAccess,
            editable && value.exitNode != null) { value = value.copy(exitNodeAllowLanAccess = it) }
        if (status?.peersRestricted == true) {
            Text(stringResource(R.string.tailscale_exit_node_restricted),style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = onAuthorize) { Text(stringResource(R.string.tailscale_authorize)) }
        }
    }
}

@Composable private fun ReviewDialog(review: TailscaleReview, status: TailscaleStatus?, onConfirm: () -> Unit, onDismiss: () -> Unit, busy: Boolean) {
    val plan = review.plan
    val logout = plan?.action == TailscaleAction.Logout
    ConfirmDangerousDialog(stringResource(if (logout) R.string.tailscale_logout else R.string.tailscale_preview),
        stringResource(if (logout) R.string.tailscale_logout_warning else if (plan != null) R.string.tailscale_change_warning else R.string.tailscale_close_warning),
        stringResource(if (logout) R.string.tailscale_logout else if (plan != null) R.string.tailscale_submit else R.string.tailscale_close),
        onConfirm = onConfirm,onDismiss = onDismiss,busy = busy,
        extraContent = {
            Text(review.owner.workspaceName)
            Text(review.owner.serviceId)
            plan?.let { value ->
                Text(actionLabel(value.action)); Text(value.operationId)
                when (value.action) {
                    TailscaleAction.Logout -> Text(stringResource(R.string.tailscale_logout_effect))
                    TailscaleAction.ConfigurePreferences -> {
                        Text(stringResource(R.string.tailscale_before))
                        PreferenceSummary(review.baseline?.preferences,status,review.baseline?.platform)
                        Text(stringResource(R.string.tailscale_after))
                        PreferenceSummary(value.preferences,status,review.baseline?.platform)
                    }
                    else -> {
                        Text(stringResource(R.string.tailscale_before))
                        Text(stringResource(if (requireNotNull(review.baseline).connected) R.string.tailscale_connected else R.string.tailscale_disconnected))
                        ConfigurationText(review.baseline.configuration)
                        Text(stringResource(R.string.tailscale_after))
                        ConfigurationText(value.configuration ?: review.baseline.configuration)
                        Text(stringResource(if (value.action == TailscaleAction.Connect || value.action == TailscaleAction.Configure && review.baseline.connected) R.string.tailscale_connected else R.string.tailscale_disconnected))
                    }
                }
            }
            review.recovery?.let { recovery ->
                Text(recovery.operationId)
                recovery.observation?.let { observation ->
                    Text(stringResource(if (!observation.authenticated) R.string.tailscale_needs_login
                        else if (observation.connected) R.string.tailscale_connected else R.string.tailscale_disconnected))
                    PreferenceSummary(observation.preferences,status,observation.platform)
                }
            }
        })
}

@Composable private fun PreferenceSummary(value: TailscalePreferences?, status: TailscaleStatus?, platform: String?) {
    if (value == null) return
    val on = stringResource(R.string.tailscale_enabled); val off = stringResource(R.string.tailscale_disabled)
    fun flag(input: Boolean) = if (input) on else off
    Text(stringResource(R.string.tailscale_preference_line,stringResource(R.string.tailscale_accept_dns),flag(value.acceptDns)))
    Text(stringResource(R.string.tailscale_preference_line,stringResource(R.string.tailscale_accept_routes),flag(value.acceptRoutes)))
    Text(stringResource(R.string.tailscale_preference_line,stringResource(R.string.tailscale_preference_incoming),
        stringResource(if (value.shieldsUp) R.string.tailscale_blocked else R.string.tailscale_allowed)))
    Text(stringResource(R.string.tailscale_preference_line,stringResource(R.string.tailscale_exit_node_title),
        value.exitNode?.let { exitNodeLabel(it,status) } ?: stringResource(R.string.tailscale_exit_node_none)))
    Text(stringResource(R.string.tailscale_preference_line,stringResource(R.string.tailscale_preference_lan_access),flag(value.exitNodeAllowLanAccess)))
    if (platform == "windows") {
        Text(stringResource(R.string.tailscale_preference_line,stringResource(R.string.tailscale_preference_unattended),flag(value.unattended)))
    }
    Text(stringResource(R.string.tailscale_preference_line,stringResource(R.string.tailscale_preference_auto_update),flag(value.autoUpdate)))
    Text(stringResource(R.string.tailscale_preference_line,stringResource(R.string.tailscale_preference_advertise_exit_node),flag(value.advertiseExitNode)))
}

@Composable private fun exitNodeLabel(value: String, status: TailscaleStatus?): String = when {
    value == "auto:any" -> stringResource(R.string.tailscale_exit_node_auto)
    else -> status?.peers?.firstOrNull { it.addresses.contains(value) }?.hostName ?: value
}

@Composable private fun PreferenceSwitch(title: String, value: Boolean, enabled: Boolean, change: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(),horizontalArrangement = Arrangement.SpaceBetween) {
        Text(title,Modifier.weight(1f)); Switch(checked = value,onCheckedChange = change,enabled = enabled,
            modifier = Modifier.semantics { contentDescription = title })
    }
}

@Composable private fun actionLabel(action: TailscaleAction) = stringResource(when(action) {
    TailscaleAction.Connect -> R.string.tailscale_connect
    TailscaleAction.Disconnect -> R.string.tailscale_disconnect
    TailscaleAction.Configure -> R.string.tailscale_configure
    TailscaleAction.ConfigurePreferences -> R.string.tailscale_edit_preferences
    TailscaleAction.Logout -> R.string.tailscale_logout
})

@Composable private fun ConfigurationText(config: TailscaleConfiguration) {
    Text(stringResource(R.string.tailscale_configuration_summary,
        stringResource(if (config.acceptDns) R.string.tailscale_enabled else R.string.tailscale_disabled),
        stringResource(if (config.acceptRoutes) R.string.tailscale_enabled else R.string.tailscale_disabled)))
}
