package app.relaxkonos.mobile.ui.common

import app.relaxkonos.mobile.ui.common.RefreshProgressIndicator

import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import androidx.annotation.StringRes
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
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.ui.theme.Spacing

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
    val files = appContainer().files
    val elevations = appContainer().elevationAnswers
    var path by remember(initialPath, kind) {
        mutableStateOf(when {
            initialPath.isBlank() -> ""
            kind == RemotePathKind.Directory -> initialPath
            else -> files.navigationParentOf(initialPath)
        })
    }
    var listing by remember(path) { mutableStateOf<app.relaxkonos.mobile.core.net.DirectoryListing?>(null) }
    var error by remember { mutableStateOf<UiMessage?>(null) }
    var loading by remember { mutableStateOf(true) }
    var refresh by remember { mutableIntStateOf(0) }

    LaunchedEffect(path, refresh) {
        loading = true
        error = null
        listing = null
        when (val result = files.list(path, elevations)) {
            is ApiResult.Success -> listing = result.value
            else -> error = result.failureMessage()
        }
        loading = false
    }

    Dialog(onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        Surface(Modifier.fillMaxSize().safeDrawingPadding(), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.fillMaxSize().padding(Spacing.lg),
                verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(title), style = MaterialTheme.typography.titleMedium,
                        modifier = Modifier.weight(1f))
                    TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) }
                }
                Text(path.ifBlank { stringResource(R.string.remote_path_roots) },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    if (path.isNotBlank() && files.navigationParentOf(path) != path) {
                        TextButton(onClick = { path = files.navigationParentOf(path) }) {
                            Text(stringResource(R.string.common_back))
                        }
                    }
                    val currentDirectory = listing?.path?.takeIf(String::isNotBlank)
                    if (kind == RemotePathKind.Directory && currentDirectory != null) {
                        Button(onClick = { onSelect(currentDirectory) }) {
                            Text(stringResource(R.string.remote_path_select_directory))
                        }
                    }
                }
                RefreshProgressIndicator(visible = loading)
                OperationMessageDialog(error?.takeUnless { loading }?.let { stringResource(R.string.remote_path_load_failed, it.text()) }, onDismiss = { error = null }, onRetry = { refresh++ })
                listing?.let { result ->
                    val entries = result.entries.filter { it.isDirectory ||
                        (kind == RemotePathKind.File && fileFilter(it.name)) }
                    if (entries.isEmpty()) Text(stringResource(emptyMessage))
                    else LazyColumn(Modifier.weight(1f)) {
                        items(entries, key = { it.path }) { entry ->
                            TextButton(onClick = {
                                if (entry.isDirectory) path = entry.path else onSelect(entry.path)
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
