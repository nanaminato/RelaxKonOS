package app.relaxkonos.mobile.ui.common

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.annotation.StringRes
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.platform.LocalContext
import app.relaxkonos.mobile.data.ElevationAnswerProvider
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.ui.theme.Spacing
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons

enum class RemotePathKind { File, Directory }

/** Keeps manual entry available while offering a browser for paths on the connected server. */
@Composable
fun RemotePathField(
    value: String,
    onValueChange: (String) -> Unit,
    @StringRes label: Int,
    kind: RemotePathKind,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    var browsing by remember { mutableStateOf(false) }
    Column(modifier, verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        OutlinedTextField(value, onValueChange, label = { Text(stringResource(label)) },
            singleLine = true, modifier = Modifier.fillMaxWidth(), enabled = enabled)
        OutlinedButton(onClick = { browsing = true }, enabled = enabled) {
            Text(stringResource(R.string.remote_path_browse))
        }
    }
    if (browsing && enabled) RemotePathPicker(
        kind = kind,
        initialPath = value,
        title = label,
        onDismiss = { browsing = false },
        onSelect = { onValueChange(it); browsing = false },
    )
}

@Composable
fun RemotePathPicker(
    kind: RemotePathKind,
    initialPath: String = "",
    @StringRes title: Int,
    onDismiss: () -> Unit,
    onSelect: (String) -> Unit,
    fileFilter: (String) -> Boolean = { true },
    @StringRes emptyMessage: Int = R.string.remote_path_empty,
) {
    val container = appContainer()
    val files = container.files
    val elevations = container.elevationAnswers
    val purpose = LocalContext.current.resources.getResourceEntryName(title) + ":" + kind.name
    val memory = remember(container, purpose) { container.usageMemory.capture(container.activeSession) { container.activeSession } }
    val remembered = remember(memory) { memory.directory(purpose, true) }
    var restoring by remember(memory, initialPath) { mutableStateOf(initialPath.isBlank() && !remembered.isNullOrBlank()) }
    var path by remember(initialPath, kind, memory) {
        mutableStateOf(when {
            initialPath.isBlank() -> remembered.orEmpty()
            kind == RemotePathKind.Directory -> initialPath
            else -> files.navigationParentOf(initialPath)
        })
    }
    val select: (String) -> Unit = { selected ->
        if (memory.isCurrent) {
            memory.rememberDirectory(purpose, true, if (kind == RemotePathKind.Directory) selected else files.navigationParentOf(selected))
            onSelect(selected)
        } else onDismiss()
    }
    var listing by remember(path) { mutableStateOf<app.relaxkonos.mobile.core.net.DirectoryListing?>(null) }
    var error by remember { mutableStateOf<UiMessage?>(null) }
    var loading by remember { mutableStateOf(true) }
    var refresh by remember { mutableIntStateOf(0) }
    var query by remember(path) { mutableStateOf("") }
    val parent = files.navigationParentOf(path)
    val canGoUp = path.isNotBlank() && parent != path

    LaunchedEffect(path, refresh) {
        loading = true
        error = null
        listing = null
        if (!memory.isCurrent) { onDismiss(); return@LaunchedEffect }
        val restoringDirectory = restoring
        restoring = false
        val result = files.list(path, if (restoringDirectory) ElevationAnswerProvider.Declines else elevations)
        if (!memory.isCurrent) { onDismiss(); return@LaunchedEffect }
        when (result) {
            is ApiResult.Success -> listing = result.value
            else -> if (restoringDirectory && path.isNotBlank()) {
                path = ""
            } else error = result.failureMessage()
        }
        loading = false
    }

    Dialog(onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        BackHandler(enabled = canGoUp) { path = parent }
        Surface(Modifier.fillMaxSize().safeDrawingPadding(), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.fillMaxSize().padding(horizontal = Spacing.lg),
                verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(title), style = MaterialTheme.typography.titleLarge,
                        modifier = Modifier.weight(1f))
                    TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) }
                }
                Surface(color = MaterialTheme.colorScheme.surfaceContainer,
                    shape = MaterialTheme.shapes.medium) {
                    Row(Modifier.fillMaxWidth().padding(Spacing.xs), verticalAlignment = Alignment.CenterVertically) {
                        IconButton(onClick = { path = parent }, enabled = canGoUp) {
                            DesktopIcon(DesktopIcons.back, contentDescription = stringResource(R.string.common_back))
                        }
                        Text((listing?.path ?: path).ifBlank { stringResource(R.string.remote_path_roots) },
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.weight(1f), maxLines = 2, overflow = TextOverflow.Ellipsis)
                        IconButton(onClick = { refresh++ }, enabled = !loading) {
                            DesktopIcon(DesktopIcons.refresh, contentDescription = stringResource(R.string.common_refresh))
                        }
                    }
                }
                OutlinedTextField(value = query, onValueChange = { query = it },
                    label = { Text(stringResource(R.string.common_search)) }, singleLine = true,
                    modifier = Modifier.fillMaxWidth())
                RefreshProgressIndicator(visible = loading)
                OperationMessageDialog(error?.takeUnless { loading }?.let { stringResource(R.string.remote_path_load_failed, it.text()) }, onDismiss = { error = null }, onRetry = { refresh++ })
                Box(Modifier.weight(1f).fillMaxWidth()) {
                  listing?.let { result ->
                    val entries = result.entries.filter { it.isDirectory ||
                        (kind == RemotePathKind.File && fileFilter(it.name)) }
                        .filter { it.name.contains(query, ignoreCase = true) }
                        .sortedWith(compareByDescending<app.relaxkonos.mobile.core.net.RemoteEntry> { it.isDirectory }.thenBy(String.CASE_INSENSITIVE_ORDER) { it.name })
                    if (entries.isEmpty()) Text(stringResource(if (query.isBlank()) emptyMessage else R.string.remote_path_no_matches),
                        modifier = Modifier.align(Alignment.Center), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    else key(path, refresh, query) { LazyColumn(Modifier.fillMaxSize()) {
                        items(entries, key = { it.path }) { entry ->
                            ListItem(modifier = Modifier.clickable {
                                if (entry.isDirectory) path = entry.path else select(entry.path)
                            }, headlineContent = {
                                Text(entry.name, maxLines = 2, overflow = TextOverflow.Ellipsis)
                            }, leadingContent = {
                                DesktopIcon(if (entry.isDirectory) DesktopIcons.folder else DesktopIcons.file, size = 28.dp)
                            }, colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surface))
                            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
                        }
                    } }
                  }
                }
                if (kind == RemotePathKind.Directory) {
                    HorizontalDivider()
                    val currentDirectory = listing?.path?.takeIf(String::isNotBlank)
                    Button(onClick = { currentDirectory?.let(select) },
                        enabled = !loading && currentDirectory != null,
                        modifier = Modifier.fillMaxWidth().padding(bottom = Spacing.sm)) {
                        Text(stringResource(R.string.remote_path_select_directory))
                    }
                }
            }
        }
    }
}
