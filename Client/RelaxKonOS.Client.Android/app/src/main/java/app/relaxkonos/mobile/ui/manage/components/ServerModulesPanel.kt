package app.relaxkonos.mobile.ui.manage.components

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Visible/resumed observation and explicit frozen-plan submission; committed references outlive the UI. */
@Composable
internal fun ServerModulesPanel(owner: SessionState.Active, onBack: () -> Unit, modifier: Modifier = Modifier, initialModuleId: String? = null) {
    val container = appContainer()
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val scope = rememberCoroutineScope()
    var facts by remember(owner) { mutableStateOf<List<ServerModuleFact>?>(null) }
    var status by remember(owner) { mutableStateOf("") }
    var operations by remember(owner) { mutableStateOf("") }
    var revision by remember(owner) { mutableIntStateOf(0) }
    var search by remember(owner) { mutableStateOf("") }
    var onlyPending by remember(owner) { mutableStateOf(false) }
    var policyEditing by remember(owner) { mutableStateOf(false) }
    var policyStrategy by remember(owner) { mutableStateOf("IdleDelayed") }
    var policyGrace by remember(owner) { mutableStateOf("300") }
    var selected by remember(owner) { mutableStateOf(setOf<String>()) }
    var detail by remember(owner, initialModuleId) { mutableStateOf(initialModuleId) }
    var plan by remember(owner) { mutableStateOf<ServerModulePlan?>(null) }
    var planExpired by remember(owner) { mutableStateOf(false) }
    var busy by remember(owner) { mutableStateOf(false) }
    var actionJob by remember(owner) { mutableStateOf<Job?>(null) }
    LaunchedEffect(plan?.id) {
        planExpired = false
        plan?.let {
            val remaining = java.time.Duration.between(java.time.Instant.now(), java.time.OffsetDateTime.parse(it.expiresAt).toInstant()).toMillis()
            delay(remaining.coerceAtLeast(0)); planExpired = true
        }
    }
    DisposableEffect(owner, lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_PAUSE) { actionJob?.cancel(); plan = null; policyEditing = false }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); actionJob?.cancel() }
    }
    BackHandler(plan != null || policyEditing || detail != null) { if (plan != null) plan = null else if (policyEditing) policyEditing = false else detail = null }
    LaunchedEffect(owner, lifecycle, revision) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            facts = null
            while (true) {
                if (!busy && plan == null) {
                    busy = true
                    try {
                        when (val result = container.serverModules.read(owner)) {
                            is ApiResult.Success -> {
                                facts = result.value
                                val recovered = container.serverModules.recover(owner, result.value)
                                if (recovered.isNotEmpty()) operations = recovered.joinToString("\n") { original ->
                                    original.operation?.let { operation ->
                                        val policyMatches = original.plan.policy == null || original.plan.moduleIds.all { id -> result.value.singleOrNull { it.id == id }?.effectivePolicy == original.plan.policy }
                                        val matches = original.plan.action in setOf("Wake", "TrySleep") || policyMatches && result.value.filter { it.effectiveEnabled }.map { it.id }.toSet() == operation.enabledIds.toSet()
                                        "${operation.id} · ${if (operation.state == "Succeeded" && !matches) "module-effective-target-mismatch" else operation.state} · ${operation.problemCode ?: ""}\n" +
                                            operation.moduleResults.joinToString("\n") { "${it.moduleId} · ${it.succeeded} · ${it.problemCode ?: ""}" }
                                    } ?: "${original.plan.id} · module-not-submitted"
                                }
                                status = if (detail != null && result.value.none { it.id == detail }) "module-not-found" else ""
                            }
                            is ApiResult.Problem -> { facts = null; status = result.code }
                            is ApiResult.Transport -> { facts = null; status = "module-state-unavailable" }
                        }
                    } catch (cancelled: CancellationException) { throw cancelled }
                    catch (_: Exception) { facts = null; status = "module-state-unavailable" }
                    finally { busy = false }
                }
                delay(3000)
            }
        }
    }
    fun preview(action: String, strategy: String? = null, grace: Int? = null, targetIds: Set<String> = selected) {
        val snapshot = facts ?: return
        if (busy) return
        busy = true
        actionJob = scope.launch {
            try {
                when (val result = container.serverModules.previewAction(owner, snapshot, targetIds, action, strategy, grace)) {
                    is ApiResult.Success -> plan = result.value
                    is ApiResult.Problem -> status = result.code
                    is ApiResult.Transport -> status = "module-state-unavailable"
                }
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { status = "module-state-unavailable" }
            finally { busy = false }
        }
    }
    if (policyEditing) AlertDialog(onDismissRequest = { policyEditing = false },
        title = { Text(stringResource(R.string.server_modules_policy)) },
        text = { Column(Modifier.heightIn(max = 400.dp).verticalScroll(rememberScrollState())) {
            listOf("KeepLoaded" to R.string.server_modules_strategy_keep_loaded, "IdleDelayed" to R.string.server_modules_strategy_idle_delayed,
                "ReleaseOnLogout" to R.string.server_modules_strategy_release_on_logout).forEach { (value, label) ->
                Row { RadioButton(selected = policyStrategy == value, onClick = { policyStrategy = value }); Text(stringResource(label)) }
            }
            OutlinedTextField(value = policyGrace, onValueChange = { policyGrace = it }, label = { Text(stringResource(R.string.server_modules_grace)) })
            Text(stringResource(R.string.server_modules_restart_note))
        } },
        confirmButton = { TextButton(enabled = !busy && policyGrace.toIntOrNull()?.let { it in 0..604800 } == true, onClick = {
            policyEditing = false; preview("SetResidencyPolicy", policyStrategy, policyGrace.toInt())
        }) { Text(stringResource(R.string.server_modules_policy_preview)) } },
        dismissButton = { TextButton(onClick = { policyEditing = false }) { Text(stringResource(R.string.common_cancel)) } })
    plan?.let { review ->
        AlertDialog(onDismissRequest = { if (!busy) plan = null },
            title = { Text(stringResource(R.string.server_modules_review)) },
            text = { Column(Modifier.heightIn(max = 460.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(review.serviceId)
                Text("${review.action}: ${review.moduleIds.joinToString()} · restart=${review.restartRequired}")
                review.policy?.let { Text("${it.strategy} · ${it.idleGraceSeconds}s") }
                Text("+ ${review.added.joinToString()}\n− ${review.removed.joinToString()}")
                Text(stringResource(R.string.server_modules_restart_note))
                Text(review.expiresAt)
                review.blockers.forEach { Text(it) }
            } },
            confirmButton = { TextButton(enabled = !busy && !planExpired && review.blockers.isEmpty(), onClick = {
                busy = true
                actionJob = scope.launch {
                    try {
                        operations = "module-awaiting-reconnection"
                        when (val result = container.serverModules.apply(owner, review)) {
                            is ApiResult.Success -> { operations = "${result.value.id} · ${result.value.state}"; plan = null; selected = emptySet(); revision++ }
                            is ApiResult.Problem -> { status = result.code; plan = null }
                            is ApiResult.Transport -> { status = "module-state-unavailable"; plan = null; revision++ }
                        }
                    } catch (cancelled: CancellationException) { throw cancelled }
                    catch (_: Exception) { status = "module-state-unavailable"; plan = null }
                    finally { busy = false }
                }
            }) { Text(stringResource(R.string.server_modules_confirm)) } },
            dismissButton = { TextButton(enabled = !busy, onClick = { plan = null }) { Text(stringResource(R.string.common_cancel)) } })
    }
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(R.string.server_modules_title), onBack = { if (detail != null) detail = null else onBack() })
        Text(stringResource(R.string.server_modules_restart_note))
        OutlinedButton(enabled = !busy, onClick = { plan = null; revision++ }) { Text(stringResource(R.string.common_refresh)) }
        if (status.isNotEmpty()) { Text(stringResource(if (status == "module-not-found") R.string.server_modules_not_found else R.string.server_modules_unknown)); Text(status) }
        if (operations.isNotEmpty()) Text(operations, Modifier.heightIn(max = 120.dp).verticalScroll(rememberScrollState()))
        facts?.let { rows ->
            if (rows.any { it.recoveryState == "host-recovery" }) {
                Text(stringResource(R.string.server_modules_recovery_mode))
                Button(enabled = !busy && rows.all { "Enable" in it.allowedActions }, onClick = {
                    preview("Enable", targetIds = rows.map { it.id }.toSet())
                }) { Text(stringResource(R.string.server_modules_restore_all)) }
            }
            Text(stringResource(R.string.server_modules_summary, rows.first().serviceId,
                rows.count { it.effectiveEnabled }, rows.size, rows.count { it.residencyState in setOf("active", "resident") }))
            OutlinedTextField(value = search, onValueChange = { search = it }, label = { Text(stringResource(R.string.server_modules_search)) })
            Row { Switch(checked = onlyPending, onCheckedChange = { onlyPending = it }); Text(stringResource(R.string.server_modules_pending)) }
            BoxWithConstraints(Modifier.weight(1f)) {
                val filtered = rows.filter { it.id.contains(search, ignoreCase = true) && (!onlyPending || it.restartRequired) }
                if (maxWidth >= 720.dp) {
                    Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            filtered.forEach { row ->
                                Row {
                                    Checkbox(checked = row.id in selected, enabled = !busy && row.allowedActions.isNotEmpty(), onCheckedChange = {
                                        selected = if (it) selected + row.id else selected - row.id; plan = null
                                    })
                                    TextButton(onClick = { detail = row.id }) { Text(row.id) }
                                }
                            }
                        }
                        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            (filtered.firstOrNull { it.id == detail } ?: filtered.firstOrNull())?.let { row ->
                                SectionCard(title = row.id) { ServerModuleFacts(row, true) }
                            }
                        }
                    }
                } else Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                filtered.filter { detail == null || it.id == detail }.forEach { row ->
                    SectionCard(title = row.id) {
                        Row {
                            Checkbox(checked = row.id in selected, enabled = !busy && row.allowedActions.isNotEmpty(), onCheckedChange = {
                                selected = if (it) selected + row.id else selected - row.id; plan = null
                            })
                            TextButton(onClick = { detail = row.id }) { Text(stringResource(R.string.server_modules_details)) }
                        }
                        ServerModuleFacts(row, detail != null)
                    }
                }
            }
            }
            val targets = rows.filter { it.id in selected }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                OutlinedButton(enabled = !busy && targets.isNotEmpty() && targets.all { "Enable" in it.allowedActions } && targets.any { !it.desiredEnabled }, onClick = { preview("Enable") }) { Text(stringResource(R.string.server_modules_enable)) }
                OutlinedButton(enabled = !busy && targets.isNotEmpty() && targets.all { "Disable" in it.allowedActions } && targets.any { it.desiredEnabled }, onClick = { preview("Disable") }) { Text(stringResource(R.string.server_modules_disable)) }
                OutlinedButton(enabled = !busy && targets.isNotEmpty() && targets.all { "Wake" in it.allowedActions }, onClick = { preview("Wake") }) { Text(stringResource(R.string.server_modules_wake)) }
                OutlinedButton(enabled = !busy && targets.isNotEmpty() && targets.all { "TrySleep" in it.allowedActions }, onClick = { preview("TrySleep") }) { Text(stringResource(R.string.server_modules_sleep)) }
                OutlinedButton(enabled = !busy && targets.isNotEmpty() && targets.all { "SetResidencyPolicy" in it.allowedActions }, onClick = {
                    targets.first().desiredPolicy?.let { policyStrategy = it.strategy; policyGrace = it.idleGraceSeconds.toString() }
                    policyEditing = true
                }) { Text(stringResource(R.string.server_modules_policy)) }
            }
        }
    }
}

@Composable
private fun ServerModuleFacts(row: ServerModuleFact, detailed: Boolean) {
    Text(stringResource(R.string.server_modules_states, row.desiredEnabled.toString(), row.effectiveEnabled.toString(), row.restartRequired.toString()))
    Text("${row.installedVersion ?: "—"} · ${row.residencyState ?: "—"} · ${row.strategy ?: "—"} · ${row.activeLeases ?: 0}")
    if (detailed) {
        Text("${row.desiredPolicy?.strategy ?: "—"} / ${row.desiredPolicy?.idleGraceSeconds ?: "—"}s → ${row.effectivePolicy?.strategy ?: "—"} / ${row.effectivePolicy?.idleGraceSeconds ?: "—"}s")
        Text(stringResource(R.string.server_modules_dependencies, row.requires.joinToString(), row.requiredBy.joinToString()))
        row.blockedBy?.takeIf { it.isNotBlank() }?.let { Text(it) }
        row.failure?.takeIf { it.isNotBlank() }?.let { Text(it) }
        Text("${row.recoveryState} · ${row.observedAt ?: "—"}")
        Text("${row.lastUsedAt ?: "—"} · ${row.lastReleasedAt ?: "—"} · ${row.lastWakeMilliseconds ?: "—"} ms")
        row.deniedActions.forEach { Text(it) }
    }
}
