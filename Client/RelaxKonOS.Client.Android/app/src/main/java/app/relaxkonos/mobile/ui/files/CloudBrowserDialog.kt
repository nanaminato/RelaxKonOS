package app.relaxkonos.mobile.ui.files

import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import java.io.File

/** One dialog instance owns one connection. SAF results never use a subsequently selected account. */
@Composable
fun CloudBrowserDialog(container: AppContainer, connection: CloudConnection, onDismiss: () -> Unit) {
    val identity by container.session.state.collectAsState()
    key(identity, connection.id) { CloudBrowserContent(container, connection, identity, onDismiss) }
}

@Composable
private fun CloudBrowserContent(container: AppContainer, connection: CloudConnection, identity: Any, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val lifetime = remember { CloudAuthorizationLifetime(identity) }
    DisposableEffect(Unit) { onDispose { lifetime.close() } }
    var path by remember { mutableStateOf(listOf("")) }
    var page by remember { mutableStateOf<CloudDirectory?>(null) }
    var busy by remember { mutableStateOf(true) }
    var notice by remember { mutableStateOf<Int?>(null) }
    var pendingDownload by remember { mutableStateOf<CloudEntry?>(null) }
    var pendingUpload by remember { mutableStateOf<String?>(null) }
    suspend fun <T> request(call: suspend (String, String) -> ApiResult<T>): ApiResult<T> =
        container.session.authenticated { url, token ->
            if (lifetime.live(container.session.state.value)) call(url, token) else ApiResult.Transport(null)
        }
    suspend fun load(reference: String, cursor: String? = null, targetPath: List<String> = path) {
        busy = true
        notice = null
        page = null
        val result = request { u, t -> container.gateway.cloudEntries(u, t, connection.id, reference, cursor) }
        if (!lifetime.live(container.session.state.value)) return
        when (result) {
            is ApiResult.Success -> { page = result.value; path = targetPath }
            else -> { page = null; notice = R.string.cloud_files_failed }
        }
        busy = false
    }
    val save = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        val entry = pendingDownload
        pendingDownload = null
        if (uri == null) busy = false
        if (uri != null && entry != null && lifetime.live(container.session.state.value)) scope.launch {
            busy = true
            val result = request { u, t -> container.gateway.cloudDownload(u, t, connection.id, entry.reference) {
                check(lifetime.live(container.session.state.value))
                requireNotNull(context.contentResolver.openOutputStream(uri, "wt"))
            } }
            if (lifetime.live(container.session.state.value)) {
                notice = if (result is ApiResult.Success) R.string.cloud_download_done else R.string.cloud_files_failed
                busy = false
            }
        }
    }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        val reference = pendingUpload
        pendingUpload = null
        if (uri == null) busy = false
        if (uri != null && reference != null && lifetime.live(container.session.state.value)) scope.launch {
            busy = true
            var staged: File? = null
            try {
                val name = withContext(Dispatchers.IO) {
                    context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
                        if (it.moveToFirst()) it.getString(0) else null
                    } ?: error("No document name")
                }
                val file = withContext(Dispatchers.IO) {
                    File.createTempFile("cloud-upload-", ".tmp", context.cacheDir).also { target ->
                        staged = target
                        requireNotNull(context.contentResolver.openInputStream(uri)).use { input -> target.outputStream().use { output ->
                            val buffer = ByteArray(64 * 1024)
                            var length = 0L
                            while (true) {
                                currentCoroutineContext().ensureActive()
                                val count = input.read(buffer)
                                if (count < 0) break
                                length += count
                                require(length <= 1024L * 1024 * 1024) // bound temporary storage to 1 GiB
                                output.write(buffer, 0, count)
                            }
                        } }
                    }
                }
                val result = request { u, t -> container.gateway.cloudUpload(u, t, connection.id, reference, name, file) }
                if (lifetime.live(container.session.state.value)) {
                    notice = if (result is ApiResult.Success) R.string.cloud_upload_queued else R.string.cloud_files_failed
                }
            } catch (error: CancellationException) { throw error
            } catch (error: Exception) { if (lifetime.live(container.session.state.value)) notice = R.string.cloud_files_failed
            } finally { staged?.delete(); busy = false }
        }
    }
    LaunchedEffect(Unit) { load("") }
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text("${connection.name} (${connection.protocol})") },
        text = { Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(stringResource(R.string.cloud_files_bound))
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            notice?.let { Text(stringResource(it)) }
            Row {
                TextButton(enabled = !busy && path.size > 1, onClick = {
                    if (busy) return@TextButton
                    busy = true
                    val target = path.dropLast(1)
                    scope.launch { load(target.last(), targetPath = target) }
                }) { Text(stringResource(R.string.cloud_parent)) }
                TextButton(enabled = !busy, onClick = { if (!busy) { busy = true; scope.launch { load(path.last()) } } }) { Text(stringResource(R.string.cloud_refresh)) }
                TextButton(enabled = !busy && connection.canWrite && pendingUpload == null && pendingDownload == null, onClick = {
                    if (busy) return@TextButton
                    busy = true; pendingUpload = path.last(); pick.launch(arrayOf("*/*"))
                }) { Text(stringResource(R.string.cloud_upload)) }
            }
            LazyColumn(Modifier.heightIn(max = 320.dp)) {
                items(page?.entries ?: emptyList()) { entry ->
                    TextButton(enabled = !busy && pendingUpload == null && pendingDownload == null, onClick = {
                        if (busy) return@TextButton
                        if (entry.isDirectory) {
                            busy = true
                            val target = path + entry.reference
                            scope.launch { load(entry.reference, targetPath = target) }
                        } else { busy = true; pendingDownload = entry; save.launch(entry.name) }
                    }) { Text((if (entry.isDirectory) "▸ " else "↓ ") + entry.name) }
                }
            }
            page?.nextCursor?.let { cursor ->
                TextButton(enabled = !busy, onClick = { if (!busy) { busy = true; scope.launch { load(path.last(), cursor) } } }) { Text(stringResource(R.string.cloud_next_page)) }
            }
        } },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cloud_close)) } })
}
