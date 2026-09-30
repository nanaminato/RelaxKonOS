package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
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

@Composable internal fun TunnelInstallEditor(model: TunnelsViewModel, dismiss: () -> Unit) {
    val container = appContainer(); val owner = remember { container.activeSession }
    val state = model.state; val original = model.currentIntent; val request = original?.request as? FrpInstallationRequest
    var kind by remember { mutableStateOf(original?.kind ?: InstallationKind.Install) }
    var version by remember { mutableStateOf(request?.version.orEmpty()) }
    var source by remember { mutableIntStateOf(if (request?.fileReferenceId != null) 1 else 0) }
    var rollback by remember { mutableStateOf(request?.rollback == true) }
    var remotePath by remember { mutableStateOf("") }; var confirmed by remember { mutableStateOf(false) }
    val locked = state.busy || model.hasIntent
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { if (it != null && container.activeSession === owner && !model.hasIntent) model.upload(it) }
    val initialId = remember { state.installation?.operationId }
    LaunchedEffect(state.installation?.operationId) { if (state.installation != null && state.installation.operationId != initialId) dismiss() }
    AlertDialog(onDismissRequest = dismiss, modifier = Modifier.imePadding(), title = { Text(stringResource(R.string.tunnels_runtime_manage)) },
        text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.tunnels_install_note))
            InstallationKind.entries.forEach { option -> Row { RadioButton(kind == option, { kind = option; rollback = false; source = 0; model.clearReference() }, enabled = !locked); Text(installationKindLabel(option)) } }
            if (kind == InstallationKind.Repair) TunnelCheck(rollback, !locked, R.string.tunnels_rollback) { rollback = it; model.clearReference() }
            if (rollback) Text(stringResource(R.string.tunnels_rollback_note))
            if (!rollback && kind != InstallationKind.Uninstall) {
                TunnelText(version, !locked, R.string.tunnels_version) { version = it; model.clearReference() }
                TextButton(enabled = !locked && version.isNotBlank(), onClick = { model.download(version.trim()) }) { Text(stringResource(R.string.tunnels_release_check)) }
                when (val download = state.download) {
                    is ApiResult.Success -> { Text(stringResource(R.string.tunnels_release_trusted, download.value.version)); Text(download.value.url, style = MaterialTheme.typography.bodySmall) }
                    null -> Unit
                    else -> Text(stringResource(R.string.tunnels_release_missing), color = MaterialTheme.colorScheme.error)
                }
            }
            if (!rollback && kind == InstallationKind.Install) {
                listOf(R.string.tunnels_source_host, R.string.tunnels_source_server, R.string.tunnels_source_phone).forEachIndexed { index, label -> Row {
                    RadioButton(source == index, { source = index; model.clearReference() }, enabled = !locked); Text(stringResource(label))
                } }
                if (source == 1) {
                    if (!locked) RemotePathField(remotePath, { remotePath = it; model.clearReference() }, R.string.tunnels_package_path, RemotePathKind.File) else Text(remotePath)
                    OutlinedButton(enabled = !locked && remotePath.isNotBlank(), onClick = { model.reference(remotePath) }) { Text(stringResource(R.string.tunnels_package_reference)) }
                }
                if (source == 2) OutlinedButton(enabled = !locked, onClick = { picker.launch(arrayOf("application/zip", "application/gzip", "application/x-gzip", "application/octet-stream")) }) { Text(stringResource(R.string.tunnels_package_pick)) }
                state.reference?.let { Text(stringResource(R.string.tunnels_package_ready, it.fileName, it.length)); if (it.expired()) Text(stringResource(R.string.tunnels_package_expired), color = MaterialTheme.colorScheme.error) }
            }
            state.uploadBytes?.let { Text(stringResource(R.string.nginx_upload_bytes, it)) }
            if (state.pendingInstallation) Text(stringResource(R.string.tunnels_install_restore_note), color = MaterialTheme.colorScheme.error)
            state.problemCode?.let { Text(tunnelProblemLabel(it), color = MaterialTheme.colorScheme.error) }
            if (state.uncertain) Text(stringResource(R.string.tunnels_uncertain), color = MaterialTheme.colorScheme.error)
            if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            TunnelCheck(confirmed, !state.busy, R.string.tunnels_install_confirm) { confirmed = it }
        } }, confirmButton = { Button(enabled = !state.busy && confirmed && state.installation?.state?.active != true &&
            (model.hasIntent || ((rollback || kind == InstallationKind.Uninstall || version.trim().matches(Regex("[A-Za-z0-9][A-Za-z0-9._-]{0,31}"))) &&
                (kind != InstallationKind.Install || source == 0 || state.reference?.expired() == false))),
            onClick = { model.install(kind, version.takeUnless { rollback || kind == InstallationKind.Uninstall }, rollback, kind == InstallationKind.Install && source != 0) }) {
            Text(stringResource(if (model.hasIntent || state.pendingInstallation) R.string.common_retry else R.string.tunnels_confirm))
        } }, dismissButton = { TextButton(enabled = !state.busy, onClick = dismiss) { Text(stringResource(R.string.common_close)) } })
}
