package app.relaxkonos.mobile.ui.manage.tailscale

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
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
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

@OptIn(ExperimentalLayoutApi::class)
@Composable fun TailscaleScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
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
    var draft by remember(owner) { mutableStateOf<TailscaleConfiguration?>(null) }
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
    LaunchedEffect(owner,state.owner) { if (owner != null && visible) model.loadRecords() }
    LaunchedEffect(owner,login.state.owner) { if (owner != null && login.state.owner === owner) login.loadRecords() }
    WorkspaceColumn(stringResource(R.string.tailscale_title),onBack,
        listOf(WorkspaceDestination("devices",R.string.tailscale_peers),WorkspaceDestination("control",R.string.tailscale_control),WorkspaceDestination("login",R.string.tailscale_login_title),WorkspaceDestination("records",R.string.tailscale_records)),
        page,{ if (!state.busy && !login.state.busy) { model.dismiss(); login.dismiss(); page = it } },modifier,stateKey = owner) {
        if (owner == null || ServerCapabilities.TAILSCALE !in owner.capabilities) {
            Text(stringResource(R.string.error_capability_missing)); return@WorkspaceColumn
        }
        if (page == "devices") TailscaleDevicesContent()
        if (page == "login" && login.state.owner === owner) TailscaleLoginContent(login,model::invalidateControl)
        if (visible && page != "devices" && page != "login") {
            Text(owner.workspaceName)
            RefreshProgressIndicator(visible = state.busy)
            if (state.problem != null) Text(stringResource(when(state.problem) {
                "tailscale.storage_failed" -> R.string.tailscale_storage_failed
                "tailscale.plan_expired" -> R.string.tailscale_plan_expired
                else -> R.string.tailscale_unverified
            }),color = MaterialTheme.colorScheme.error)
            if (state.records.any { it.phase != TailscaleReferencePhase.Known }) Text(stringResource(R.string.tailscale_pending))
            if (page == "control") {
                Text(stringResource(R.string.tailscale_control_intro))
                TextButton(enabled = !state.busy,onClick = model::loadControl) { Text(stringResource(R.string.tailscale_load_control)) }
                state.control?.let { control ->
                    ManagementCard {
                        Text(stringResource(if (control.connected) R.string.tailscale_connected else R.string.tailscale_disconnected))
                        ConfigurationText(control.configuration)
                        if (!control.canControl) Text(stringResource(R.string.tailscale_control_unavailable))
                    }
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        Button(enabled = model.editable && control.authenticated && !control.connected,onClick = { model.prepare(TailscaleAction.Connect) }) { Text(stringResource(R.string.tailscale_connect)) }
                        OutlinedButton(enabled = model.editable && control.connected,onClick = { model.prepare(TailscaleAction.Disconnect) }) { Text(stringResource(R.string.tailscale_disconnect)) }
                        OutlinedButton(enabled = model.editable,onClick = { draft = control.configuration }) { Text(stringResource(R.string.tailscale_configure)) }
                    }
                }
            }
            if (page == "records") {
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
                        Text(stringResource(if (observation.connected) R.string.tailscale_connected else R.string.tailscale_disconnected))
                        ConfigurationText(observation.configuration)
                    }
                    TextButton(enabled = !state.busy && recovery.canClose && owner.privilegedOperations,onClick = model::requestClose) { Text(stringResource(R.string.tailscale_close)) }
                }
            }
        }
    }
    val config = draft
    if (config != null && visible) AlertDialog(onDismissRequest = { draft = null },title = { Text(stringResource(R.string.tailscale_configure)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState())) {
            ConfigurationSwitch(stringResource(R.string.tailscale_accept_dns),config.acceptDns) { draft = config.copy(acceptDns = it) }
            ConfigurationSwitch(stringResource(R.string.tailscale_accept_routes),config.acceptRoutes) { draft = config.copy(acceptRoutes = it) }
        } },confirmButton = { Button(enabled = model.editable && config != state.control?.configuration,onClick = { draft = null; model.prepare(TailscaleAction.Configure,config) }) { Text(stringResource(R.string.tailscale_preview)) } },
        dismissButton = { TextButton(onClick = { draft = null }) { Text(stringResource(R.string.common_cancel)) } })
    val review = model.review?.takeIf { visible && it.owner === owner }
    if (review != null) ConfirmDangerousDialog(stringResource(R.string.tailscale_preview),
        stringResource(if (review.plan != null) R.string.tailscale_change_warning else R.string.tailscale_close_warning),
        stringResource(if (review.plan != null) R.string.tailscale_submit else R.string.tailscale_close),
        onConfirm = { model.submit(review) },onDismiss = model::dismiss,busy = state.busy,
        extraContent = {
            Text(review.owner.workspaceName)
            Text(review.owner.serviceId)
            review.plan?.let { plan ->
                Text(actionLabel(plan.action)); Text(plan.operationId)
                Text(stringResource(R.string.tailscale_before))
                Text(stringResource(if (requireNotNull(review.baseline).connected) R.string.tailscale_connected else R.string.tailscale_disconnected))
                ConfigurationText(review.baseline.configuration)
                Text(stringResource(R.string.tailscale_after)); ConfigurationText(plan.configuration ?: review.baseline.configuration)
                Text(stringResource(if (plan.action == TailscaleAction.Connect || plan.action == TailscaleAction.Configure && review.baseline.connected) R.string.tailscale_connected else R.string.tailscale_disconnected))
            }
            review.recovery?.let { recovery ->
                Text(recovery.operationId)
                recovery.observation?.let { Text(stringResource(if (it.connected) R.string.tailscale_connected else R.string.tailscale_disconnected)); ConfigurationText(it.configuration) }
            }
        })
}
@Composable private fun ConfigurationSwitch(title: String, value: Boolean, change: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(),horizontalArrangement = Arrangement.SpaceBetween) { Text(title,Modifier.weight(1f)); Switch(checked = value,onCheckedChange = change,modifier = Modifier.semantics { contentDescription = title }) }
}
@Composable private fun ConfigurationText(config: TailscaleConfiguration) {
    Text(stringResource(R.string.tailscale_configuration_summary,
        stringResource(if (config.acceptDns) R.string.tailscale_enabled else R.string.tailscale_disabled),
        stringResource(if (config.acceptRoutes) R.string.tailscale_enabled else R.string.tailscale_disabled)))
}
@Composable private fun actionLabel(action: TailscaleAction) = stringResource(when(action) {
    TailscaleAction.Connect -> R.string.tailscale_connect; TailscaleAction.Disconnect -> R.string.tailscale_disconnect; TailscaleAction.Configure -> R.string.tailscale_configure
})
