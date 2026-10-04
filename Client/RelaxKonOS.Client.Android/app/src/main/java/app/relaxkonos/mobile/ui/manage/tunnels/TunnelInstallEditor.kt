package app.relaxkonos.mobile.ui.manage.tunnels

import app.relaxkonos.mobile.ui.common.rememberUsageOpenDocument

import androidx.activity.compose.rememberLauncherForActivityResult
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

@Composable internal fun TunnelInstallEditor(model: TunnelsViewModel, onSubmitted: () -> Unit, dismiss: () -> Unit) {
    val container = appContainer(); val owner = remember { container.activeSession }
    val state = model.state; val original = model.currentIntent; val request = original?.request as? FrpInstallationRequest
    var kind by remember { mutableStateOf(original?.kind ?: InstallationKind.Install) }
    var version by remember { mutableStateOf(request?.version.orEmpty()) }
    var source by remember { mutableStateOf(if (request?.fileReferenceId != null) InstallationPackageSource.ServerFile else InstallationPackageSource.HostDownload) }
    var rollback by remember { mutableStateOf(request?.rollback == true) }
    var remotePath by remember { mutableStateOf("") }; var confirmed by remember { mutableStateOf(false) }
    val locked = state.busy || model.hasIntent
    val picker = rememberLauncherForActivityResult(rememberUsageOpenDocument("TunnelInstallEditor.package")) { if (it != null && container.activeSession === owner && !model.hasIntent) model.upload(it) }
    val initialId = remember { state.installation?.operationId }
    LaunchedEffect(state.installation?.operationId) { if (state.installation != null && state.installation.operationId != initialId) dismiss() }
    AlertDialog(onDismissRequest = dismiss, modifier = Modifier.imePadding(), title = { Text(stringResource(R.string.tunnels_runtime_manage)) },
        text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.tunnels_install_note))
            InstallationKind.entries.forEach { option -> Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { RadioButton(kind == option, { kind = option; rollback = false; source = InstallationPackageSource.HostDownload; model.clearReference() }, enabled = !locked); Text(installationKindLabel(option)) } }
            if (kind == InstallationKind.Repair) TunnelCheck(rollback, !locked, R.string.tunnels_rollback) { rollback = it; model.clearReference() }
            if (rollback) Text(stringResource(R.string.tunnels_rollback_note))
            if (!rollback && kind != InstallationKind.Uninstall) {
                TunnelText(version, !locked, R.string.tunnels_version) { version = it; model.clearReference() }
                if (kind == InstallationKind.Install) ManualPackageDownload(version.trim(), !locked, state.busy,
                    (state.download as? ApiResult.Success)?.value?.takeIf { it.version == version.trim() }?.url,
                    onRequest = { model.download(version.trim()) })
            }
            if (!rollback && kind == InstallationKind.Install) {
                InstallationPackagePicker(source, !locked,
                    { source = it; model.clearReference() }, remotePath,
                    { remotePath = it; model.clearReference() }, { model.reference(remotePath) },
                    { picker.launch(arrayOf("application/zip", "application/gzip", "application/x-gzip", "application/octet-stream")) },
                    state.reference)
            }
            state.uploadBytes?.let { Text(stringResource(R.string.nginx_upload_bytes, it)) }
            if (state.pendingInstallation) Text(stringResource(R.string.tunnels_install_restore_note), color = MaterialTheme.colorScheme.error)

            RefreshProgressIndicator(visible = state.busy)
            TunnelCheck(confirmed, !state.busy, R.string.tunnels_install_confirm) { confirmed = it }
        } }, confirmButton = { Button(enabled = !state.busy && confirmed && state.installation?.state?.active != true &&
            (model.hasIntent || ((rollback || kind == InstallationKind.Uninstall || version.trim().matches(Regex("[A-Za-z0-9][A-Za-z0-9._-]{0,31}"))) &&
                (kind != InstallationKind.Install || source == InstallationPackageSource.HostDownload || state.reference?.expired() == false))),
            onClick = { onSubmitted(); model.install(kind, version.takeUnless { rollback || kind == InstallationKind.Uninstall }, rollback, kind == InstallationKind.Install && source != InstallationPackageSource.HostDownload) }) {
            Text(stringResource(if (model.hasIntent || state.pendingInstallation) R.string.common_retry else R.string.tunnels_confirm))
        } }, dismissButton = { TextButton(enabled = !state.busy, onClick = dismiss) { Text(stringResource(R.string.common_close)) } })
}
