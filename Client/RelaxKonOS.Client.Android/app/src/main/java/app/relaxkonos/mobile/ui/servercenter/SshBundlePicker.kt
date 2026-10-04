package app.relaxkonos.mobile.ui.servercenter

import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.relaxkonos.mobile.ui.common.RefreshProgressIndicator

import app.relaxkonos.mobile.ui.common.ActionLabel
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.compose.ui.platform.LocalContext
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.theme.Spacing

/** Browses the already trusted SSH workspace using the same SFTP session rules as its Files tab. */
@Composable
internal fun SshBundlePicker(hostId: String, onDismiss: () -> Unit, onSelect: (String) -> Unit) {
    val app = LocalContext.current.applicationContext as RelaxKonApplication
    val container = app.container
    val purpose = "ssh.bundle.$hostId"
    val memory = remember(container, hostId) { container.usageMemory.capture(container.activeSession) { container.activeSession } }
    val remembered = remember(memory) { memory.directory(purpose, true) }
    var restoreStarted by remember(hostId) { mutableStateOf(false) }
    var restoring by remember(hostId) { mutableStateOf(false) }
    val model = remember(app, hostId) { SshFilesController(app) }
    DisposableEffect(model) { onDispose { model.close() } }
    val state by model.state.collectAsStateWithLifecycle()
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val revision = (LocalContext.current.applicationContext as RelaxKonApplication).container.serverCenter.workspaceRevision
    var resumed by remember(lifecycle) { mutableStateOf(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
    DisposableEffect(lifecycle, model) {
        val observer = LifecycleEventObserver { _, _ -> resumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); model.stop() }
    }
    LaunchedEffect(hostId, revision, resumed) { if (resumed) model.resume(hostId) else model.stop() }
    LaunchedEffect(state.connected, state.busy, state.problem, remembered) {
        if (!memory.isCurrent) { onDismiss(); return@LaunchedEffect }
        if (!restoreStarted && state.connected && !state.busy) {
            restoreStarted = true
            if (!remembered.isNullOrBlank() && remembered != state.path) {
                restoring = true
                model.navigate(remembered)
            }
        } else if (restoring && !state.busy) {
            restoring = false
            if (state.problem != null) model.navigate("/")
        }
    }

    Dialog(onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        Surface(Modifier.fillMaxSize().safeDrawingPadding(), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.fillMaxSize().padding(Spacing.lg),
                verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.ssh_workspace_deploy_remote_bundle_path),
                        style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                    TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) }
                }
                Text(state.path, style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (state.path != "/") TextButton(onClick = model::up, enabled = !state.busy) {
                    Text(stringResource(R.string.common_back))
                }
                RefreshProgressIndicator(visible = state.busy)
                state.problem?.let {
                    Text(stringResource(R.string.remote_path_load_failed,
                        stringResource(R.string.ssh_files_connection_failed)),
                        color = MaterialTheme.colorScheme.error)
                    TextButton(onClick = model::reload) { ActionLabel(R.string.common_retry) }
                }
                if (state.connected && !state.busy && state.problem == null) {
                    val entries = state.entries.filter { !it.isSymbolicLink &&
                        (it.isDirectory || it.name.endsWith(".zip", ignoreCase = true)) }
                    if (entries.isEmpty()) Text(stringResource(R.string.ssh_bundle_folder_empty))
                    else LazyColumn(Modifier.weight(1f)) {
                        items(entries, key = { it.path }) { entry ->
                            TextButton(onClick = {
                                if (entry.isDirectory) model.open(entry) else if (memory.isCurrent) {
                                    memory.rememberDirectory(purpose, true, entry.path.substringBeforeLast('/').ifBlank { "/" })
                                    onSelect(entry.path)
                                } else onDismiss()
                            }, modifier = Modifier.fillMaxWidth()) {
                                Text(if (entry.isDirectory) "${entry.name}/" else entry.name,
                                    modifier = Modifier.weight(1f))
                            }
                        }
                    }
                }
            }
        }
    }
}
