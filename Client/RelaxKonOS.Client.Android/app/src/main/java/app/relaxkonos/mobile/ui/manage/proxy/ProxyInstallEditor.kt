package app.relaxkonos.mobile.ui.manage.proxy

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

@Composable internal fun ProxyInstallEditor(model: ProxyViewModel, onSubmitted: () -> Unit, dismiss: () -> Unit) {
    val container = appContainer(); val owner = remember { container.activeSession }
    val state = model.state; val original = model.currentIntent; val request = original?.request as? MihomoInstallationRequest
    var kind by remember { mutableStateOf(original?.kind ?: InstallationKind.Install) }
    var version by remember { mutableStateOf(request?.version.orEmpty()) }
    var source by remember { mutableStateOf(if (request?.fileReferenceId != null) InstallationPackageSource.ServerFile else InstallationPackageSource.HostDownload) }
    var rollback by remember { mutableStateOf(request?.rollback == true) }
    var remotePath by remember { mutableStateOf("") }; var confirmed by remember { mutableStateOf(false) }
    val locked = state.busy || model.hasIntent
    val releases = (state.releases as? ApiResult.Success)?.value.orEmpty()
    var expanded by remember { mutableStateOf(false) }
    LaunchedEffect(state.busy, state.releases) {
        if (!state.busy && state.releases == null && !model.hasIntent) model.loadReleases()
    }
    LaunchedEffect(state.releases) {
        if (!model.hasIntent && version.isBlank()) {
            version = (releases.firstOrNull { it.recommended } ?: releases.firstOrNull())?.version.orEmpty()
        }
    }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { if (it != null && container.activeSession === owner && !model.hasIntent) model.upload(it) }
    val initialId = remember { state.installation?.operationId }
    LaunchedEffect(state.installation?.operationId) { if (state.installation != null && state.installation.operationId != initialId) dismiss() }
    AlertDialog(onDismissRequest = dismiss, modifier = Modifier.imePadding(), title = { Text(stringResource(R.string.mihomo_runtime_manage)) },
        text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.mihomo_install_note))
            InstallationKind.entries.forEach { option -> Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { RadioButton(kind == option, { kind = option; rollback = false; source = InstallationPackageSource.HostDownload; model.clearReference() }, enabled = !locked); Text(installationKindLabel(option)) } }
            if (kind == InstallationKind.Repair) ProxyCheck(rollback, !locked, R.string.tunnels_rollback) { rollback = it; model.clearReference() }
            if (rollback) Text(stringResource(R.string.tunnels_rollback_note))
            if (!rollback && kind != InstallationKind.Uninstall) {
                Box {
                    OutlinedButton(enabled = !locked && releases.isNotEmpty(), onClick = { expanded = true }) {
                        Text(if (releases.any { it.version == version && it.recommended })
                            stringResource(R.string.mihomo_release_recommended, version)
                        else version.ifBlank { stringResource(R.string.mihomo_release_select) })
                    }
                    DropdownMenu(expanded = expanded && !locked, onDismissRequest = { expanded = false }) {
                        releases.forEach { release ->
                            DropdownMenuItem(text = { Text(if (release.recommended) stringResource(R.string.mihomo_release_recommended, release.version) else release.version) },
                                onClick = { version = release.version; expanded = false; model.clearReference() })
                        }
                    }
                }
                TextButton(enabled = !locked, onClick = { model.loadReleases() }) { Text(stringResource(R.string.mihomo_releases_refresh)) }
                when (state.releases) {
                    is ApiResult.Success -> if (releases.isEmpty()) Text(stringResource(R.string.mihomo_releases_empty))
                    null -> Unit
                    else -> Text(stringResource(R.string.mihomo_releases_failed), color = MaterialTheme.colorScheme.error)
                }
                if (kind == InstallationKind.Install) ManualPackageDownload(version, !locked, state.busy,
                    releases.firstOrNull { it.version == version }?.url,
                    onRequest = { if (releases.none { it.version == version }) model.loadReleases() })
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
            ProxyCheck(confirmed, !state.busy, R.string.mihomo_install_confirm) { confirmed = it }
        } }, confirmButton = { Button(enabled = !state.busy && confirmed && state.installation?.state?.active != true &&
            (model.hasIntent || ((rollback || kind == InstallationKind.Uninstall || releases.any { it.version == version }) &&
                (kind != InstallationKind.Install || source == InstallationPackageSource.HostDownload || state.reference?.expired() == false))),
            onClick = { onSubmitted(); model.install(kind, version.takeUnless { rollback || kind == InstallationKind.Uninstall }, rollback, kind == InstallationKind.Install && source != InstallationPackageSource.HostDownload) }) {
            Text(stringResource(if (model.hasIntent || state.pendingInstallation) R.string.common_retry else R.string.tunnels_confirm))
        } }, dismissButton = { TextButton(enabled = !state.busy, onClick = dismiss) { Text(stringResource(R.string.common_close)) } })
}

