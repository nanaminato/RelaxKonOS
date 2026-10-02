package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*

@Composable internal fun proxyRuntimeLabel(state: ProxyRuntimeState) = stringResource(when (state) {
    ProxyRuntimeState.NotInstalled -> R.string.mihomo_state_notinstalled
    ProxyRuntimeState.Installing -> R.string.mihomo_state_installing
    ProxyRuntimeState.Stopped -> R.string.mihomo_state_stopped
    ProxyRuntimeState.Starting -> R.string.mihomo_state_starting
    ProxyRuntimeState.Running -> R.string.mihomo_state_running
    ProxyRuntimeState.Reloading -> R.string.mihomo_state_reloading
    ProxyRuntimeState.Stopping -> R.string.mihomo_state_stopping
    ProxyRuntimeState.Updating -> R.string.mihomo_state_updating
    ProxyRuntimeState.Recovering -> R.string.mihomo_state_recovering
    ProxyRuntimeState.Degraded -> R.string.mihomo_state_degraded
    ProxyRuntimeState.Failed -> R.string.mihomo_state_failed
})
@Composable internal fun proxyOperationLabel(state: ProxyOperationState) = stringResource(when (state) {
    ProxyOperationState.Queued -> R.string.mihomo_state_queued
    ProxyOperationState.Running -> R.string.mihomo_state_running
    ProxyOperationState.Succeeded -> R.string.mihomo_state_succeeded
    ProxyOperationState.Failed -> R.string.mihomo_state_failed
    ProxyOperationState.Cancelled -> R.string.mihomo_state_cancelled
    ProxyOperationState.Interrupted -> R.string.mihomo_state_interrupted
})
@Composable internal fun proxyStageLabel(stage: String) = stringResource(when (stage) {
    "queued" -> R.string.mihomo_state_queued; "running" -> R.string.mihomo_state_running
    "completed" -> R.string.mihomo_state_succeeded; "failed" -> R.string.mihomo_state_failed
    "interrupted" -> R.string.mihomo_state_interrupted; else -> R.string.mihomo_unverified
})
@Composable internal fun proxyActionLabel(action: ProxyAction) = stringResource(when (action) {
    ProxyAction.Start -> R.string.mihomo_state_start; ProxyAction.Stop -> R.string.mihomo_state_stop; ProxyAction.Restart -> R.string.mihomo_state_restart
    ProxyAction.RefreshSubscription -> R.string.common_refresh; ProxyAction.ActivateSubscription -> R.string.mihomo_activate
    ProxyAction.RefreshAll -> R.string.mihomo_refresh_all
    ProxyAction.EnableTun -> R.string.mihomo_tun_enable; ProxyAction.DisableTun -> R.string.mihomo_tun_disable; ProxyAction.EmergencyDisableTun -> R.string.mihomo_tun_emergency
})
@Composable internal fun proxyRoutingLabel(mode: ProxyRoutingMode) = stringResource(when (mode) {
    ProxyRoutingMode.Rule -> R.string.mihomo_state_rule; ProxyRoutingMode.Global -> R.string.mihomo_state_global; ProxyRoutingMode.Direct -> R.string.mihomo_state_direct
})
@Composable internal fun proxyProblemLabel(code: String) = stringResource(when {
    code == "proxy.system_proxy_conflict" -> R.string.mihomo_system_proxy_conflict
    code.startsWith("proxy.runtime_") || code == "proxy.external_runtime_invalid" -> R.string.mihomo_problem_runtime
    code.startsWith("proxy.config_") -> R.string.mihomo_problem_config
    code.startsWith("proxy.subscription_") -> R.string.mihomo_problem_subscription
    code.startsWith("proxy.controller_") -> R.string.mihomo_controller_unavailable
    code in setOf("proxy.permission_denied", "proxy.not_supported", "proxy.platform_capability_unavailable", "proxy.privileged_operation_unavailable", "proxy.tun_permission_required") -> R.string.mihomo_problem_permission
    code in setOf("proxy.recovery_required", "proxy.recovery_failed", "proxy.management_route_unsafe", "proxy.tun_activation_failed") -> R.string.mihomo_problem_recovery
    code == "proxy.port_in_use" -> R.string.mihomo_problem_port
    code == "proxy.operation_interrupted" -> R.string.mihomo_problem_interrupted
    else -> R.string.mihomo_problem_unknown
})
@Composable internal fun proxyTunLabel(state: String) = stringResource(when (state) {
    "disabled" -> R.string.mihomo_tun_disabled; "enabled" -> R.string.mihomo_tun_enabled
    "enabling" -> R.string.mihomo_tun_enabling; "disabling" -> R.string.mihomo_tun_disabling
    "recovering" -> R.string.mihomo_state_recovering; "failed" -> R.string.mihomo_state_failed; else -> R.string.mihomo_unverified
})
@Composable internal fun proxyLogLevelLabel(level: String) = stringResource(when (level) {
    "silent" -> R.string.mihomo_silent; "error" -> R.string.mihomo_error; "warning" -> R.string.mihomo_warning
    "info" -> R.string.mihomo_info; "debug" -> R.string.mihomo_debug; else -> R.string.mihomo_unverified
})
