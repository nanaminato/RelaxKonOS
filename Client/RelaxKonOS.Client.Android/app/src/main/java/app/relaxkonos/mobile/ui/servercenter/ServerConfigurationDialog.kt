package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.ServerHostSnapshot
import app.relaxkonos.mobile.servercenter.ServerInstallMode
import app.relaxkonos.mobile.ui.common.CheckboxOption
import java.net.URI
import androidx.compose.ui.unit.dp

internal data class ServerConfigurationDraft(val reviewed: ServerHostSnapshot, val port: Int, val lan: Boolean, val guardian: Boolean, val autoStart: Boolean? = null, val fileAccess: String? = null, val fileRoots: List<String>? = null)

/** In-memory fields belong to one maintenance editor; no credentials or process-persistent state. */
internal class ServerConfigurationFields(reviewed: ServerHostSnapshot) {
    val endpoint = runCatching { URI(reviewed.listenUrl) }.getOrNull()
    val port = mutableStateOf(endpoint?.port?.toString().orEmpty())
    val lan = mutableStateOf(endpoint?.host == "0.0.0.0")
    val guardian = mutableStateOf(reviewed.guardianEnabled == true)
    val access = mutableStateOf(reviewed.fileAccess)
    val roots = mutableStateOf(reviewed.fileRoots?.joinToString("\n").orEmpty())
    val autoStart = mutableStateOf(reviewed.autoStart == true)
    val discard = mutableStateOf(false)
}

@Composable
internal fun ServerConfigurationDialog(editor: MaintenanceConfiguration, busy: Boolean, interactionVersion: Long,
    onDismiss: () -> Unit, onApply: (ServerConfigurationDraft) -> Unit) {
    val reviewed = editor.snapshot
    val componentsOnly = editor.componentsOnly
    val fields = editor.fields
    val endpoint = fields.endpoint
    var port by fields.port
    var lan by fields.lan
    var guardian by fields.guardian
    var access by fields.access
    var roots by fields.roots
    var autoStart by fields.autoStart
    var discard by fields.discard
    val valid = endpoint?.host in listOf("127.0.0.1", "0.0.0.0") &&
        endpoint?.scheme in listOf("http", "https") && (port.toIntOrNull() ?: 0) in 1..65535 && reviewed.guardianEnabled != null && (access != "whitelist" || roots.lines().filter { it.isNotBlank() }.let { paths -> paths.isNotEmpty() && paths.all { it.trim().startsWith("/") || Regex("^[A-Za-z]:[\\\\/].+").matches(it.trim()) } })
    val changed = port != endpoint?.port?.toString() || lan != (endpoint?.host == "0.0.0.0") || guardian != reviewed.guardianEnabled || access != reviewed.fileAccess || roots != reviewed.fileRoots?.joinToString("\n").orEmpty() || reviewed.autoStart != null && autoStart != reviewed.autoStart
    val close = { if (!busy) { if (changed) discard = true else onDismiss() } }
    AlertDialog(onDismissRequest = close,
        title = { Text(stringResource(if (componentsOnly) R.string.server_configuration_components else R.string.server_configuration_settings)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(reviewed.listenUrl.orEmpty())
            if (componentsOnly) {
                Text(stringResource(when (reviewed.guardianEnabled) {
                    true -> R.string.server_guardian_current_installed
                    false -> R.string.server_guardian_current_absent
                    null -> R.string.server_guardian_current_unknown
                }))
                if (reviewed.guardianEnabled != null) {
                    val installed = reviewed.guardianEnabled == true
                    CheckboxOption(guardian != installed, stringResource(if (installed) R.string.server_guardian_remove else R.string.server_guardian_choice), !busy) { guardian = if (installed) !it else it }
                    Text(stringResource(if (guardian) R.string.server_guardian_target_installed else R.string.server_guardian_target_absent))
                    if (installed && !guardian) Text(stringResource(R.string.server_guardian_remove_note))
                }
            }
            else {
                Text(stringResource(when (reviewed.mode) {
                    ServerInstallMode.LinuxUser -> R.string.server_configuration_mode_user
                    ServerInstallMode.LinuxSystem, ServerInstallMode.WindowsSystem -> R.string.server_configuration_mode_system
                    null -> R.string.server_configuration_mode_unknown
                }))
                Text(stringResource(R.string.server_configuration_note))
                OutlinedTextField(port, { port = it }, enabled = !busy, label = { Text(stringResource(R.string.server_configuration_port)) })
                if (reviewed.fileAccess != null) {
                    listOf("restricted" to R.string.ssh_workspace_deploy_restricted, "whitelist" to R.string.server_install_file_access_whitelist, "full" to R.string.ssh_workspace_deploy_full_access).forEach { (value, label) ->
                        Row { RadioButton(access == value, onClick = { access = value }, enabled = !busy); Text(stringResource(label)) }
                    }
                    if (access == "whitelist") OutlinedTextField(roots, { roots = it }, enabled = !busy, label = { Text(stringResource(R.string.server_install_file_roots)) })
                }
                if (reviewed.autoStart != null) CheckboxOption(autoStart, stringResource(when (reviewed.mode) {
                    ServerInstallMode.LinuxUser -> R.string.server_configuration_auto_start_user
                    ServerInstallMode.LinuxSystem, ServerInstallMode.WindowsSystem -> R.string.server_configuration_auto_start_system
                    null -> R.string.server_configuration_auto_start
                }), !busy) { autoStart = it }
                CheckboxOption(lan, stringResource(R.string.server_configuration_lan), !busy) { lan = it }
            }
        } },
        confirmButton = { key(interactionVersion) { TextButton(onClick = { onApply(ServerConfigurationDraft(reviewed, requireNotNull(port.toIntOrNull()), lan, guardian, if (reviewed.autoStart != null) autoStart else null, access, if (access == "whitelist") roots.lines().map(String::trim).filter(String::isNotEmpty) else null)) }, enabled = !busy && valid && changed) {
            Text(stringResource(R.string.server_configuration_apply))
        } } },
        dismissButton = { key(interactionVersion) { TextButton(onClick = close, enabled = !busy) { Text(stringResource(R.string.common_cancel)) } } })
    if (discard) AlertDialog(onDismissRequest = { discard = false },
        title = { Text(stringResource(R.string.server_configuration_discard)) },
        confirmButton = { key(interactionVersion) { TextButton(onClick = onDismiss) { Text(stringResource(R.string.server_configuration_discard_confirm)) } } },
        dismissButton = { key(interactionVersion) { TextButton(onClick = { discard = false }) { Text(stringResource(R.string.common_cancel)) } } })
}
