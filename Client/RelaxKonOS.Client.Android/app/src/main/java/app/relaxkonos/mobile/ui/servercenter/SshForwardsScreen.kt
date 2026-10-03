package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.ui.common.ActionLabel
import app.relaxkonos.mobile.ui.common.ActivityIndicator
import app.relaxkonos.mobile.ui.common.ExecutionStatusChip
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import android.Manifest
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.*
import app.relaxkonos.mobile.ui.theme.Spacing
import app.relaxkonos.mobile.data.ExternalServiceAddresses
import app.relaxkonos.mobile.ui.common.ServiceAccess

@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun SshForwardsScreen(hostId: String, modifier: Modifier = Modifier) {
    val container = (LocalContext.current.applicationContext as RelaxKonApplication).container
    val manager = container.sshForwards
    val state by manager.state.collectAsState()
    var editor by remember(hostId) { mutableStateOf<Pair<String?, SshLocalForwardRequest>?>(null) }
    var denied by remember { mutableStateOf(false) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { denied = !it }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        Text(stringResource(R.string.ssh_forward_title), style = MaterialTheme.typography.titleLarge)
        Text(stringResource(R.string.ssh_forward_note))
        if (denied) Text(stringResource(R.string.ssh_forward_permission_note))
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) TextButton(onClick = { permission.launch(Manifest.permission.POST_NOTIFICATIONS) }) { Text(stringResource(R.string.ssh_forward_notification_enable)) }
        Button(enabled = !state.busy, onClick = { editor = null to SshLocalForwardRequest(8080) }) { Text(stringResource(R.string.ssh_forward_add)) }
        if (state.busy) ActivityIndicator(stringResource(R.string.ssh_forward_busy))
        OperationMessageDialog(state.problem?.takeUnless { state.busy }?.let { problem -> stringResource(when (problem) {
            "invalid" -> R.string.ssh_forward_invalid
            "credential" -> R.string.ssh_forward_credential
            "trust" -> R.string.ssh_forward_trust
            "limit" -> R.string.ssh_forward_limit
            "test" -> R.string.ssh_forward_test_failed
            "service" -> R.string.ssh_forward_service_failed
            else -> R.string.ssh_forward_connect_failed
        }) })
        val items = state.items.filter { it.hostId == hostId }
        if (items.isEmpty()) Text(stringResource(R.string.ssh_forward_empty))
        items.forEach { row ->
            HorizontalDivider()
            Text(stringResource(R.string.ssh_forward_mapping, row.localPort, row.request.remotePort), style = MaterialTheme.typography.titleMedium)
            ExecutionStatusChip(stringResource(when (row.status) {
                SshForwardStatus.Running -> R.string.ssh_forward_running
                SshForwardStatus.Stopped -> R.string.ssh_forward_stopped
                SshForwardStatus.Disconnected -> R.string.ssh_forward_disconnected
            }), row.status.name, task = false)
            Text(row.request.localUrl(row.localPort), style = MaterialTheme.typography.bodySmall)
            ExternalServiceAddresses.forward(row)?.let { address -> ServiceAccess(listOf(address), ready = !state.busy) {
                container.serverCenter.sshFilesHostId == hostId && manager.state.value.items.firstOrNull { it.id == row.id } == row
            } }
            row.testedAtMillis?.let { time -> Text(stringResource(R.string.ssh_forward_test_result,
                stringResource(if (row.reachable == true) R.string.ssh_forward_reachable else R.string.ssh_forward_unreachable),
                java.text.DateFormat.getDateTimeInstance().format(java.util.Date(time)))) }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                OutlinedButton(enabled = !state.busy, onClick = { editor = row.id to row.request }) { Text(stringResource(R.string.ssh_forward_edit)) }
                if (row.status == SshForwardStatus.Running) {
                    OutlinedButton(enabled = !state.busy, onClick = { manager.test(row.id) }) { Text(stringResource(R.string.ssh_forward_test)) }
                    OutlinedButton(enabled = !state.busy, onClick = { manager.stop(row.id) }) { Text(stringResource(R.string.ssh_forward_stop)) }
                } else OutlinedButton(enabled = !state.busy, onClick = { manager.start(hostId, row.request, row.id) }) { Text(stringResource(R.string.ssh_forward_start)) }
                TextButton(enabled = !state.busy, onClick = { manager.remove(row.id) }) { ActionLabel(R.string.common_delete) }
            }
        }
        if (items.any { it.status == SshForwardStatus.Running } || state.busy) TextButton(onClick = manager::stopAll) { Text(stringResource(R.string.ssh_forward_stop_all)) }
    }
    editor?.let { (id, initial) ->
        SshForwardEditor(initial, id != null, !state.busy, onDismiss = { editor = null }, onSubmit = { request -> manager.start(hostId, request, id); editor = null })
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun SshForwardEditor(initial: SshLocalForwardRequest, editing: Boolean, ready: Boolean, onDismiss: () -> Unit, onSubmit: (SshLocalForwardRequest) -> Unit) {
    var remote by remember(initial) { mutableStateOf(initial.remotePort.toString()) }
    var local by remember(initial) { mutableStateOf(initial.preferredLocalPort?.toString().orEmpty()) }
    var scheme by remember(initial) { mutableStateOf(initial.scheme) }
    var path by remember(initial) { mutableStateOf(initial.pathAndQuery) }
    val request = remote.toIntOrNull()?.let { port ->
        val localPort = if (local.isBlank()) null else local.toIntOrNull() ?: return@let null
        SshLocalForwardRequest(port, localPort, scheme, path).takeIf { it.valid() }
    }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(if (editing) R.string.ssh_forward_edit else R.string.ssh_forward_add)) }, text = {
        Column(Modifier.heightIn(max = 460.dp).verticalScroll(rememberScrollState()).imePadding(), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            if (editing) Text(stringResource(R.string.ssh_forward_edit_note))
            OutlinedTextField(value = remote, onValueChange = { if (it.length <= 5) remote = it }, singleLine = true, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ssh_forward_remote_port)) })
            OutlinedTextField(value = local, onValueChange = { if (it.length <= 5) local = it }, singleLine = true, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ssh_forward_local_port)) })
            FlowRow { listOf("http", "https").forEach { value -> FilterChip(selected = scheme == value, onClick = { scheme = value }, label = { Text(value.uppercase()) }) } }
            OutlinedTextField(value = path, onValueChange = { if (it.length <= 2048) path = it }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ssh_forward_path)) })
            if (request == null) Text(stringResource(R.string.ssh_forward_invalid), color = MaterialTheme.colorScheme.error)
        }
    }, confirmButton = { TextButton(enabled = ready && request != null, onClick = { request?.let(onSubmit) }) { Text(stringResource(R.string.ssh_forward_start)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } })
}
