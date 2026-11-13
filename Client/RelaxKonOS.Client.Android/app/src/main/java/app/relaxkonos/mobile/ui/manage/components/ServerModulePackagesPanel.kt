package app.relaxkonos.mobile.ui.manage.components

import androidx.activity.compose.BackHandler
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
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.*
import java.io.File

@Composable
internal fun ServerModulePackagesPanel(owner: SessionState.Active, onBack: () -> Unit, modifier: Modifier = Modifier) {
    val container = appContainer()
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val scope = rememberCoroutineScope()
    var pickerSession by remember { mutableStateOf<String?>(null) }
    var picking by remember(owner) { mutableStateOf(false) }
    var busy by remember(owner) { mutableStateOf(false) }
    var job by remember(owner) { mutableStateOf<Job?>(null) }
    var plan by remember(owner) { mutableStateOf<ModulePackagePlan?>(null) }
    var expired by remember(owner) { mutableStateOf(false) }
    var status by remember(owner) { mutableStateOf("") }
    var query by remember(owner) { mutableStateOf("") }
    var page by remember(owner) { mutableStateOf<ModuleStorePage?>(null) }
    fun task(action: suspend () -> Unit) {
        if (busy || !owner.controller || !lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) return
        busy = true
        job = scope.launch {
            try { action() }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { status = "module-state-unavailable" }
            finally { busy = false }
        }
    }
    fun accept(result: ApiResult<ModulePackagePlan>) {
        when (result) {
            is ApiResult.Success -> { plan = result.value; status = "" }
            is ApiResult.Problem -> status = result.code
            is ApiResult.Transport -> status = "module-state-unavailable"
        }
    }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        val sameSession = pickerSession == owner.sessionInstanceId
        pickerSession = null
        picking = false
        if (uri != null && sameSession) task {
            val file = withContext(Dispatchers.IO) { File.createTempFile("module-", ".rkmodule", context.cacheDir) }
            try {
                withContext(Dispatchers.IO) {
                    context.contentResolver.openInputStream(uri).use { input ->
                        requireNotNull(input)
                        file.outputStream().use { output ->
                            val buffer = ByteArray(65536); var size = 0L
                            while (true) {
                                currentCoroutineContext().ensureActive()
                                val count = input.read(buffer); if (count < 0) break
                                size += count; require(size <= (1L shl 30))
                                output.write(buffer, 0, count)
                            }
                            require(size > 0)
                        }
                    }
                }
                when (container.serverModules.read(owner)) {
                    is ApiResult.Success -> accept(container.serverModules.uploadPackage(owner, file))
                    else -> status = "module-state-unavailable"
                }
            } finally { withContext(NonCancellable + Dispatchers.IO) { file.delete() } }
        }
    }
    DisposableEffect(owner, lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_PAUSE) { job?.cancel(); plan = null }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); job?.cancel() }
    }
    LaunchedEffect(owner, lifecycle) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            while (true) {
                if (!busy && !picking && plan == null && owner.controller) {
                    busy = true
                    try {
                        if (container.serverModules.read(owner) is ApiResult.Success) {
                            val recovered = container.serverModules.recoverPackages(owner)
                            if (recovered.isNotEmpty()) status = recovered.joinToString("\n") {
                                "${it.plan.moduleId} · ${it.plan.version} · ${it.problem ?: it.operation?.state ?: if (it.plan.operationId == null) "module-not-submitted" else "module-state-unavailable"} · ${it.operation?.problem ?: ""}"
                            }
                        }
                    } catch (cancelled: CancellationException) { throw cancelled }
                    catch (_: Exception) { status = "module-state-unavailable" }
                    finally { busy = false }
                }
                delay(3000)
            }
        }
    }
    LaunchedEffect(plan?.id) {
        expired = false
        plan?.let {
            delay(java.time.Duration.between(java.time.Instant.now(), java.time.OffsetDateTime.parse(it.expiresAt).toInstant()).toMillis().coerceAtLeast(0))
            expired = true
        }
    }
    BackHandler { if (plan != null) plan = null else onBack() }
    plan?.let { review ->
        AlertDialog(onDismissRequest = { if (!busy) plan = null }, title = { Text(stringResource(R.string.server_modules_review)) },
            text = { Column(Modifier.heightIn(max = 460.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(owner.serviceId)
                Text("${review.moduleId} · ${review.version} · ${review.runtime}")
                Text("${review.sourceId} / ${review.publisherId}")
                Text(review.sha256)
                Text(stringResource(R.string.module_packages_install_note))
                review.privileges.forEach { Text(it) }
                Text(review.expiresAt)
            } }, confirmButton = { TextButton(enabled = !busy && !expired && review.operationId == null, onClick = {
                task {
                    // Persisted before dispatch; a lost reply never causes an automatic second apply.
                    status = "module-awaiting-reconnection"
                    plan = null
                    when (val result = container.serverModules.applyPackage(owner, review)) {
                        is ApiResult.Success -> status = "${result.value.id} · module-package-accepted"
                        is ApiResult.Problem -> status = result.code
                        is ApiResult.Transport -> status = "module-state-unavailable"
                    }
                }
            }) { Text(stringResource(R.string.server_modules_confirm)) } },
            dismissButton = { TextButton(enabled = !busy, onClick = { plan = null }) { Text(stringResource(R.string.common_cancel)) } })
    }
    Column(modifier.fillMaxSize().padding(Spacing.lg).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(R.string.module_packages_title), onBack = onBack)
        Text(stringResource(R.string.module_packages_install_note))
        Text(owner.serviceId)
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        if (status.isNotBlank()) Text(status)
        Button(enabled = !busy && owner.controller, onClick = { pickerSession = owner.sessionInstanceId; picking = true; picker.launch(arrayOf("application/octet-stream", "application/zip", "*/*")) }) { Text(stringResource(R.string.module_packages_upload)) }
        OutlinedButton(enabled = !busy && owner.controller, onClick = { task {
            when (container.serverModules.read(owner)) {
                is ApiResult.Success -> {
                    val recovered = container.serverModules.recoverPackages(owner)
                    status = recovered.joinToString("\n") { "${it.plan.moduleId} · ${it.plan.version} · ${it.problem ?: it.operation?.state ?: if (it.plan.operationId == null) "module-not-submitted" else "module-state-unavailable"} · ${it.operation?.problem ?: ""}" }
                }
                else -> status = "module-state-unavailable"
            }
        } }) { Text(stringResource(R.string.module_packages_receipts)) }
        OutlinedTextField(value = query, enabled = !busy, onValueChange = { query = it; page = null }, label = { Text(stringResource(R.string.server_modules_search)) })
        fun search(offset: Int) { task {
            when (container.serverModules.read(owner)) {
                is ApiResult.Success -> when (val result = container.serverModules.store(owner, query, offset)) {
                    is ApiResult.Success -> { page = result.value; status = result.value.blockers.joinToString("\n") }
                    is ApiResult.Problem -> status = result.code
                    is ApiResult.Transport -> status = "module-state-unavailable"
                }
                else -> status = "module-state-unavailable"
            }
        } }
        OutlinedButton(enabled = !busy && owner.controller, onClick = { search(0) }) { Text(stringResource(R.string.module_packages_search)) }
        page?.items?.forEach { item ->
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(Spacing.md)) {
                    Text("${item.moduleId} · ${item.version} · ${item.rid}")
                    Text("${item.sourceId} · ${item.bytes} B")
                    item.blockers.forEach { Text(it) }
                    OutlinedButton(enabled = !busy && item.blockers.isEmpty(), onClick = { task { accept(container.serverModules.downloadPackage(owner, item)) } }) {
                        Text(stringResource(R.string.module_packages_preview))
                    }
                }
            }
        }
        page?.nextOffset?.let { next -> OutlinedButton(enabled = !busy, onClick = { search(next) }) { Text(stringResource(R.string.module_packages_more)) } }
    }
}
