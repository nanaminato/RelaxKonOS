package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.TunnelMutation
import app.relaxkonos.mobile.ui.manage.operations.installationProblemLabel

@Composable internal fun tunnelConnectionLabel(state: TunnelConnectionState) = stringResource(when (state) {
    TunnelConnectionState.SavedNotApplied -> R.string.tunnels_saved
    TunnelConnectionState.Starting -> R.string.tunnels_starting
    TunnelConnectionState.Connected -> R.string.tunnels_connected
    TunnelConnectionState.Disconnected -> R.string.tunnels_disconnected
    TunnelConnectionState.RuntimeUnavailable -> R.string.tunnels_runtime_unavailable
    TunnelConnectionState.Unknown -> R.string.tunnels_unknown
})
@Composable internal fun tunnelRuntimeLabel(state: TunnelRuntimeState) = stringResource(when (state) {
    TunnelRuntimeState.NotInstalled -> R.string.tunnels_not_installed
    TunnelRuntimeState.Available -> R.string.tunnels_available
    TunnelRuntimeState.Running -> R.string.tunnels_runtime_running
    TunnelRuntimeState.Stopped -> R.string.tunnels_runtime_stopped
    TunnelRuntimeState.ExternalInvalid -> R.string.tunnels_external_invalid
    TunnelRuntimeState.Unknown -> R.string.tunnels_unknown
})
@Composable internal fun tunnelMutationLabel(action: TunnelMutation) = stringResource(when (action) {
    TunnelMutation.SaveProfile -> R.string.tunnels_profile_save
    TunnelMutation.DeleteProfile -> R.string.tunnels_profile_delete
    TunnelMutation.SetToken -> R.string.tunnels_token_set
    TunnelMutation.SaveDefinition -> R.string.tunnels_definition_save
    TunnelMutation.DeleteDefinition -> R.string.tunnels_definition_delete
    TunnelMutation.Apply -> R.string.tunnels_apply
    TunnelMutation.Stop -> R.string.tunnels_stop
    TunnelMutation.SaveFrps -> R.string.frps_save
    TunnelMutation.StartFrps -> R.string.frps_start
    TunnelMutation.StopFrps -> R.string.frps_stop
    TunnelMutation.RestartFrps -> R.string.frps_restart
})
@Composable internal fun tunnelAuthLabel(auth: TunnelAuth) = stringResource(if (auth == TunnelAuth.Token) R.string.tunnels_auth_token else R.string.tunnels_auth_none)
@Composable internal fun tunnelModeLabel(mode: TunnelRuntimeMode) = stringResource(if (mode == TunnelRuntimeMode.Managed) R.string.tunnels_managed else R.string.tunnels_external)
@Composable internal fun tunnelTlsLabel(tls: TunnelTls) = stringResource(when (tls) { TunnelTls.Default -> R.string.tunnels_tls_default; TunnelTls.Disable -> R.string.tunnels_tls_disable; TunnelTls.Force -> R.string.tunnels_tls_force })
@Composable internal fun tunnelProblemLabel(code: String): String = if (code.startsWith("installation.")) installationProblemLabel(code) else stringResource(when (code) {
    "tunnel.revision_conflict", "tunnel.profile_conflict", "tunnel.endpoint_conflict" -> R.string.tunnels_conflict
    "tunnel.observation_pending", "tunnel.applied_revision_unknown" -> R.string.tunnels_uncertain
    "tunnel.definition_not_applied" -> R.string.tunnels_saved
    "tunnel.profile_invalid", "tunnel.definition_invalid", "tunnel.auth_unsupported", "tunnel.protocol_unsupported", "tunnel.remote_port_required", "tunnel.domain_required", "tunnel.token_invalid" -> R.string.tunnels_validation
    "tunnel.frps_process_unverified" -> R.string.frps_process_unverified
    "tunnel.frps_restart_required" -> R.string.frps_restart_required
    "tunnel.frps_invalid_bind", "tunnel.frps_invalid_allow_ports", "tunnel.frps_invalid_port", "tunnel.frps_port_conflict", "tunnel.frps_secret_invalid", "tunnel.frps.confirmation_required" -> R.string.frps_invalid
    "tunnel.frps_port_in_use" -> R.string.frps_port_in_use
    "tunnel.frps_token_required", "tunnel.frps_dashboard_credentials_required" -> R.string.frps_credentials_required
    "tunnel.frps_not_configured" -> R.string.frps_not_configured
    "tunnel.frps_stop_failed" -> R.string.frps_stop_failed
    "tunnel.frps_config_verify_failed", "tunnel.frps_start_failed", "tunnel.frps_exited" -> R.string.frps_start_failed
    "tunnel.profile_in_use" -> R.string.tunnels_profile_in_use
    "tunnel.profile_not_found", "tunnel.not_found" -> R.string.tunnels_missing
    "tunnel.external_path_invalid", "tunnel.external_path_required", "tunnel.external_not_found", "tunnel.external_not_file", "tunnel.external_not_executable", "tunnel.external_probe_failed", "tunnel.external_invalid" -> R.string.tunnels_external_invalid
    "tunnel.managed_runtime_not_installed", "tunnel.managed_runtime_missing" -> R.string.tunnels_runtime_unavailable
    "tunnel.runtime_version_invalid", "tunnel.runtime_release_not_configured" -> R.string.tunnels_release_missing
    "tunnel.runtime_install_failed", "tunnel.runtime_install_timeout", "tunnel.runtime_health_check_failed", "tunnel.runtime_archive_invalid", "tunnel.runtime_archive_missing_binary", "tunnel.runtime_archive_too_large", "tunnel.runtime_archive_unexpected_entry", "tunnel.runtime_checksum_failed", "tunnel.runtime_download_failed", "tunnel.runtime_download_too_large", "tunnel.runtime_length_invalid", "tunnel.runtime_state_invalid", "tunnel.runtime_uninstall_failed" -> R.string.tunnels_install_failed
    "tunnel.runtime_no_previous_version", "tunnel.runtime_previous_unhealthy" -> R.string.tunnels_rollback_unavailable
    "tunnel.token_not_applicable", "tunnel.token_required", "tunnel.secret_unavailable" -> R.string.tunnels_token_problem
    "tunnel.authentication_failed" -> R.string.tunnels_auth_failed
    "tunnel.config_verify_failed", "tunnel.runtime_start_failed", "tunnel.runtime_exited", "tunnel.profile_stop_failed" -> R.string.tunnels_apply_failed
    "tunnel.profile_limit_exceeded", "tunnel.definition_limit_exceeded" -> R.string.tunnels_limit
    "elevation-required", "tunnel.elevation_required" -> R.string.error_elevation_required
    else -> R.string.error_generic
})

@Composable internal fun frpsStateLabel(state: ManagedFrpsState) = stringResource(when (state) {
    ManagedFrpsState.NotConfigured -> R.string.frps_not_configured
    ManagedFrpsState.Stopped -> R.string.frps_stopped
    ManagedFrpsState.Starting -> R.string.frps_starting
    ManagedFrpsState.Running -> R.string.frps_running
    ManagedFrpsState.RuntimeUnavailable -> R.string.tunnels_runtime_unavailable
    ManagedFrpsState.Failed -> R.string.frps_failed
    ManagedFrpsState.Unknown -> R.string.tunnels_unknown
})
@Composable internal fun frpsAuditAction(action: String) = stringResource(when (action) {
    "frps.configure" -> R.string.frps_audit_configure
    "frps.start" -> R.string.frps_start
    "frps.stop" -> R.string.frps_stop
    "frps.token.read" -> R.string.frps_audit_token_read
    else -> R.string.frps_audit_other
})
