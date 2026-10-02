package app.relaxkonos.mobile.ui.common

import android.content.ClipData
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.InstallationFileReference
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.launch

internal enum class InstallationPackageSource { HostDownload, ServerFile, PhoneFile }

@Composable
internal fun InstallationPackagePicker(
    source: InstallationPackageSource,
    enabled: Boolean,
    onSourceChange: (InstallationPackageSource) -> Unit,
    remotePath: String,
    onPathChange: (String) -> Unit,
    onReference: () -> Unit,
    onPick: () -> Unit,
    reference: InstallationFileReference?,
) {
    Text(stringResource(R.string.installation_package_source))
    Column(Modifier.selectableGroup()) {
        InstallationPackageSource.entries.forEach { option ->
            Row(Modifier.fillMaxWidth().selectable(source == option, enabled = enabled, role = Role.RadioButton,
                onClick = { if (source != option) onSourceChange(option) }), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                RadioButton(source == option, onClick = null, enabled = enabled)
                Text(stringResource(when (option) {
                    InstallationPackageSource.HostDownload -> R.string.installation_source_host
                    InstallationPackageSource.ServerFile -> R.string.installation_source_server
                    InstallationPackageSource.PhoneFile -> R.string.installation_source_phone
                }))
            }
        }
    }
    when (source) {
        InstallationPackageSource.HostDownload -> Unit
        InstallationPackageSource.ServerFile -> {
            RemotePathField(remotePath, onPathChange, R.string.tunnels_package_path, RemotePathKind.File, enabled = enabled)
            OutlinedButton(enabled = enabled && remotePath.isNotBlank(), onClick = onReference) {
                Text(stringResource(R.string.tunnels_package_reference))
            }
        }
        InstallationPackageSource.PhoneFile -> OutlinedButton(enabled = enabled, onClick = onPick) {
            Text(stringResource(R.string.tunnels_package_pick))
        }
    }
    if (source != InstallationPackageSource.HostDownload) reference?.let {
        Text(stringResource(R.string.tunnels_package_ready, it.fileName, it.length))
        if (it.expired()) Text(stringResource(R.string.tunnels_package_expired), color = MaterialTheme.colorScheme.error)
    }
}

/** The URL is obtained from the selected host's release API; no session headers are sent to a browser. */
@Composable
internal fun ManualPackageDownload(
    version: String,
    enabled: Boolean,
    loading: Boolean,
    url: String?,
    onRequest: () -> Unit,
) {
    var requested by remember(version) { mutableStateOf(false) }
    var copied by remember(url) { mutableStateOf(false) }
    var failed by remember(url) { mutableStateOf(false) }
    val context = LocalContext.current
    val clipboard = LocalClipboard.current
    val scope = rememberCoroutineScope()
    TextButton(enabled = enabled && version.isNotBlank(), onClick = { requested = true; onRequest() }) {
        Text(stringResource(R.string.installation_download_myself))
    }
    if (!requested) return
    when {
        loading -> Text(stringResource(R.string.installation_download_loading))
        url == null -> Text(stringResource(R.string.tunnels_release_missing), color = MaterialTheme.colorScheme.error)
        else -> {
            SelectionContainer { Text(url, style = MaterialTheme.typography.bodySmall) }
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                TextButton(onClick = { scope.launch {
                    runCatching { clipboard.setClipEntry(ClipEntry(ClipData.newPlainText("Package download", url))) }
                        .onSuccess { copied = true }.onFailure { failed = true }
                } }) { Text(stringResource(R.string.installation_download_copy)) }
                TextButton(onClick = {
                    runCatching {
                        val uri = Uri.parse(url)
                        require(uri.scheme == "https" && !uri.host.isNullOrBlank() && uri.userInfo == null)
                        context.startActivity(Intent(Intent.ACTION_VIEW, uri).addCategory(Intent.CATEGORY_BROWSABLE))
                    }.onFailure { failed = true }
                }) { Text(stringResource(R.string.installation_download_open)) }
            }
            Text(stringResource(R.string.installation_download_then_select))
            if (copied) Text(stringResource(R.string.installation_download_copied))
            if (failed) Text(stringResource(R.string.installation_download_action_failed), color = MaterialTheme.colorScheme.error)
        }
    }
}
