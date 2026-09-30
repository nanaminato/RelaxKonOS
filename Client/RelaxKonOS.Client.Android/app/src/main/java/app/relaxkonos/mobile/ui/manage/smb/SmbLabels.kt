package app.relaxkonos.mobile.ui.manage.smb

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*

@Composable internal fun smbStateLabel(state: SmbRuntimeState) = stringResource(when (state) {
    SmbRuntimeState.Unsupported -> R.string.smb_unsupported; SmbRuntimeState.NotInstalled -> R.string.smb_not_installed
    SmbRuntimeState.Stopped -> R.string.smb_stopped; SmbRuntimeState.Running -> R.string.smb_running
    SmbRuntimeState.Failed -> R.string.smb_failed; SmbRuntimeState.Unavailable -> R.string.smb_unverified
})
@Composable internal fun smbActionLabel(kind: SmbChangeKind) = stringResource(when (kind) {
    SmbChangeKind.Start -> R.string.smb_start; SmbChangeKind.Stop -> R.string.smb_stop; SmbChangeKind.Restart -> R.string.smb_restart
    SmbChangeKind.CreateShare -> R.string.smb_create; SmbChangeKind.UpdateShare -> R.string.smb_edit; SmbChangeKind.DeleteShare -> R.string.common_delete
    SmbChangeKind.EnableUser -> R.string.smb_user_enable; SmbChangeKind.DisableUser -> R.string.smb_user_disable; SmbChangeKind.Password -> R.string.smb_password
})
@Composable internal fun smbAccessLabel(access: SmbAccess) = stringResource(if (access == SmbAccess.Read) R.string.smb_access_read else R.string.smb_access_write)
@Composable internal fun smbProblem(code: String) = stringResource(when (code) {
    "file-services.platform_unsupported", "file-services.smb.windows_server_required" -> R.string.smb_unsupported
    "file-services.smb.not_installed" -> R.string.smb_not_installed
    "file-services.smb.facts_changed", "file-services.smb.share_conflict", "file-services.smb.reconciliation_required", "file-services.smb.configuration_unmanaged" -> R.string.smb_conflict
    "file-services.smb.configuration_invalid" -> R.string.smb_invalid
    "file-services.smb.credential_update_failed", "file-services.smb.system_account_not_found" -> R.string.smb_credential_failed
    "file-services.smb.elevation_required", "elevation-required", "file-services.smb.helper_unavailable" -> R.string.smb_authorization
    "file-services.smb.port_in_use", "file-services.smb.port_unavailable" -> R.string.smb_port_failed
    "file-services.smb.restart_required" -> R.string.smb_restart_required
    "file-services.smb.windows_security_configuration_required" -> R.string.smb_security_failed
    else -> R.string.smb_unverified
})
