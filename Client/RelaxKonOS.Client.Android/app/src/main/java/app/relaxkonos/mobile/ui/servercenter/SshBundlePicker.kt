package app.relaxkonos.mobile.ui.servercenter

import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.relaxkonos.mobile.ui.common.RefreshProgressIndicator

import app.relaxkonos.mobile.ui.common.ActionLabel
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
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
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.data.UsageMemoryScope
import app.relaxkonos.mobile.servercenter.SshFileEntry

/** Browses the already trusted SSH workspace using the same SFTP session rules as its Files tab. */
@Composable
internal fun SshBundlePicker(hostId: String, onDismiss: () -> Unit, onSelect: (String) -> Unit) {
    val app = LocalContext.current.applicationContext as RelaxKonApplication
    val container = app.container
    val purpose = "ssh.bundle.$hostId"
    val memory = remember(container, hostId) { container.usageMemory.capture(container.activeSession) { container.activeSession } }
    val session by container.session.state.collectAsStateWithLifecycle()
    val remembered = remember(memory) { memory.directory(purpose, true) }
    var restoreStarted by remember(hostId) { mutableStateOf(false) }
    var restoring by remember(hostId) { mutableStateOf(false) }
    val model = remember(app, hostId) { SshFilesController(app) }
    DisposableEffect(model) { onDispose { model.close() } }
    SshBundlePickerSessionGuard(session, memory, model::stop, onDismiss)
    val state by model.state.collectAsStateWithLifecycle()
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val revision = (LocalContext.current.applicationContext as RelaxKonApplication).container.serverCenter.workspaceRevision
    var resumed by remember(lifecycle) { mutableStateOf(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
    DisposableEffect(lifecycle, model) {
        val observer = LifecycleEventObserver { _, _ -> resumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); model.stop() }
    }
    LaunchedEffect(hostId, revision, resumed, session) {
        if (resumed && memory.isCurrent) model.resume(hostId) else model.stop()
    }
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

    SshBundlePickerContent(state, onDismiss,
        onUp = { if (memory.isCurrent) model.up() else onDismiss() },
        onRetry = { if (memory.isCurrent) model.reload() else onDismiss() },
        onOpen = { entry ->
            if (!memory.isCurrent) {
                model.stop()
                onDismiss()
            } else if (entry.isDirectory) model.open(entry) else {
                memory.rememberDirectory(purpose, true, entry.path.substringBeforeLast('/').ifBlank { "/" })
                onSelect(entry.path)
            }
        })
}

@Composable
internal fun SshBundlePickerSessionGuard(
    session: SessionState,
    memory: UsageMemoryScope,
    onStop: () -> Unit,
    onDismiss: () -> Unit,
) {
    LaunchedEffect(session, memory) {
        if (!memory.isCurrent) {
            onStop()
            onDismiss()
        }
    }
}

@Composable
internal fun SshBundlePickerContent(
    state: SshFilesUiState,
    onDismiss: () -> Unit,
    onUp: () -> Unit,
    onRetry: () -> Unit,
    onOpen: (SshFileEntry) -> Unit,
) {
    var showingPath by remember(state.path) { mutableStateOf(false) }
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
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2, overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.fillMaxWidth().clickable(role = Role.Button) { showingPath = true }
                        .heightIn(min = 48.dp).wrapContentHeight(Alignment.CenterVertically))
                if (state.path != "/") TextButton(onClick = onUp, enabled = !state.busy) {
                    Text(stringResource(R.string.common_back))
                }
                RefreshProgressIndicator(visible = state.busy)
                LazyColumn(Modifier.weight(1f).fillMaxWidth()) {
                    if (state.problem != null) item {
                        Column {
                            Text(stringResource(R.string.remote_path_load_failed,
                                stringResource(R.string.ssh_files_connection_failed)),
                                color = MaterialTheme.colorScheme.error)
                            TextButton(onClick = onRetry, enabled = !state.busy) { ActionLabel(R.string.common_retry) }
                        }
                    }
                    if (state.connected && !state.busy && state.problem == null) {
                        val entries = state.entries.filter { !it.isSymbolicLink &&
                            (it.isDirectory || it.name.endsWith(".zip", ignoreCase = true)) }
                        if (entries.isEmpty()) item { Text(stringResource(R.string.ssh_bundle_folder_empty)) }
                        else {
                            items(entries, key = { it.path }) { entry ->
                                TextButton(onClick = { onOpen(entry) }, modifier = Modifier.fillMaxWidth()) {
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
    if (showingPath) AlertDialog(
        onDismissRequest = { showingPath = false },
        title = { Text(stringResource(R.string.files_label_path)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState())) { Text(state.path) } },
        confirmButton = { TextButton(onClick = { showingPath = false }) { Text(stringResource(R.string.common_close)) } },
    )
}
