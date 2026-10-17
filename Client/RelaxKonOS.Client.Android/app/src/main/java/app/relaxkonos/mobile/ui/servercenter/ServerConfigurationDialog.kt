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
import app.relaxkonos.mobile.ui.common.CheckboxOption
import java.net.URI
import androidx.compose.ui.unit.dp

internal data class ServerConfigurationDraft(val reviewed: ServerHostSnapshot, val port: Int, val lan: Boolean, val guardian: Boolean, val autoStart: Boolean? = null, val fileAccess: String? = null, val fileRoots: List<String>? = null)

@Composable
internal fun ServerConfigurationDialog(reviewed: ServerHostSnapshot, componentsOnly: Boolean, busy: Boolean,
    onDismiss: () -> Unit, onApply: (ServerConfigurationDraft) -> Unit) {
    val endpoint = remember(reviewed) { runCatching { URI(reviewed.listenUrl) }.getOrNull() }
    var port by remember(reviewed) { mutableStateOf(endpoint?.port?.toString().orEmpty()) }
    var lan by remember(reviewed) { mutableStateOf(endpoint?.host == "0.0.0.0") }
    var guardian by remember(reviewed) { mutableStateOf(reviewed.guardianEnabled == true) }
    var access by remember(reviewed) { mutableStateOf(reviewed.fileAccess) }
    var roots by remember(reviewed) { mutableStateOf(reviewed.fileRoots?.joinToString("\n").orEmpty()) }
    var autoStart by remember(reviewed) { mutableStateOf(reviewed.autoStart == true) }
    var discard by remember { mutableStateOf(false) }
    val valid = endpoint?.host in listOf("127.0.0.1", "0.0.0.0") &&
        endpoint?.scheme in listOf("http", "https") && (port.toIntOrNull() ?: 0) in 1..65535 && reviewed.guardianEnabled != null && (access != "whitelist" || roots.lines().filter { it.isNotBlank() }.let { paths -> paths.isNotEmpty() && paths.all { it.trim().startsWith("/") || Regex("^[A-Za-z]:[\\\\/].+").matches(it.trim()) } })
    val changed = port != endpoint?.port?.toString() || lan != (endpoint?.host == "0.0.0.0") || guardian != reviewed.guardianEnabled || access != reviewed.fileAccess || roots != reviewed.fileRoots?.joinToString("\n").orEmpty() || reviewed.autoStart != null && autoStart != reviewed.autoStart
    val close = { if (!busy) { if (changed) discard = true else onDismiss() } }
    AlertDialog(onDismissRequest = close,
        title = { Text(stringResource(if (componentsOnly) R.string.server_configuration_components else R.string.server_configuration_settings)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(reviewed.listenUrl.orEmpty())
            Text(stringResource(R.string.server_configuration_note))
            if (componentsOnly) CheckboxOption(guardian, stringResource(R.string.server_guardian_choice), !busy) { guardian = it }
            else {
                OutlinedTextField(port, { port = it }, enabled = !busy, label = { Text(stringResource(R.string.server_configuration_port)) })
                if (reviewed.fileAccess != null) {
                    listOf("restricted" to R.string.ssh_workspace_deploy_restricted, "whitelist" to R.string.server_install_file_access_whitelist, "full" to R.string.ssh_workspace_deploy_full_access).forEach { (value, label) ->
                        Row { RadioButton(access == value, onClick = { access = value }, enabled = !busy); Text(stringResource(label)) }
                    }
                    if (access == "whitelist") OutlinedTextField(roots, { roots = it }, enabled = !busy, label = { Text(stringResource(R.string.server_install_file_roots)) })
                }
                if (reviewed.autoStart != null) CheckboxOption(autoStart, stringResource(R.string.server_configuration_auto_start), !busy) { autoStart = it }
                CheckboxOption(lan, stringResource(R.string.server_configuration_lan), !busy) { lan = it }
            }
        } },
        confirmButton = { TextButton(onClick = { onApply(ServerConfigurationDraft(reviewed, requireNotNull(port.toIntOrNull()), lan, guardian, if (reviewed.autoStart != null) autoStart else null, access, if (access == "whitelist") roots.lines().map(String::trim).filter(String::isNotEmpty) else null)) }, enabled = !busy && valid) {
            Text(stringResource(R.string.server_configuration_apply))
        } },
        dismissButton = { TextButton(onClick = close, enabled = !busy) { Text(stringResource(R.string.common_cancel)) } })
    if (discard) AlertDialog(onDismissRequest = { discard = false },
        title = { Text(stringResource(R.string.server_configuration_discard)) },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.server_configuration_discard_confirm)) } },
        dismissButton = { TextButton(onClick = { discard = false }) { Text(stringResource(R.string.common_cancel)) } })
}
