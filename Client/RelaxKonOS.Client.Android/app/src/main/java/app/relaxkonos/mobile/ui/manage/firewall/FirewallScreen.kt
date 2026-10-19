package app.relaxkonos.mobile.ui.manage.firewall

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date


@Composable fun FirewallScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val container = appContainer()
    val model: FirewallViewModel = viewModel(factory = viewModelFactory {
        initializer { FirewallViewModel(container.session, container.firewall, container.elevationAnswers) }
    })
    FirewallContent(model, container.activeSession, onBack, modifier)
}
@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun FirewallContent(model: FirewallViewModel, owner: SessionState.Active?,
    onBack: () -> Unit, modifier: Modifier = Modifier) {
    val state = model.state
    val visible = state.owner === owner
    val facts = if (visible) state.facts else null
    val ready = visible && !state.busy && owner?.privilegedOperations == true && facts?.status?.isAvailable == true && state.pending.isEmpty()
    var section by rememberSaveable(owner) { mutableStateOf("overview") }
    var draft by remember(owner) { mutableStateOf<FirewallRule?>(null) }
    var defaults by remember(owner) { mutableStateOf<Pair<String, String>?>(null) }
    var initialDraft by remember(owner) { mutableStateOf<FirewallRule?>(null) }
    var initialDefaults by remember(owner) { mutableStateOf<Pair<String, String>?>(null) }
    var selected by remember(owner) { mutableStateOf<Int?>(null) }
    val confirmation = model.confirmation?.takeIf { it.owner === owner }
    var leave by remember(owner) { mutableStateOf<(() -> Unit)?>(null) }
    val editing = draft != null || defaults != null
    val dirty = (draft != null && draft != initialDraft) || (defaults != null && defaults != initialDefaults)
    val navigate: (() -> Unit) -> Unit = { action ->
        if (!state.busy) {
            model.withdrawConfirmation()
            if (dirty) leave = action else action()
        }
    }
    LaunchedEffect(owner, state.owner) { if (owner != null && visible) model.refresh() }
    LaunchedEffect(owner, state.saved) { if (visible && state.saved > 0) { draft = null; defaults = null } }
    DisposableEffect(owner) { onDispose { model.stop() } }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    DisposableEffect(owner, lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_PAUSE) { model.withdrawConfirmation(); leave = null }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer) }
    }
    BackHandler(editing) { navigate { draft = null; defaults = null } }

    WorkspaceColumn(stringResource(R.string.firewall_title), { navigate(onBack) }, listOf(WorkspaceDestination("overview", R.string.workspace_overview), WorkspaceDestination("records", R.string.workspace_records)), section,
        { destination -> navigate { draft = null; defaults = null; section = destination } }, modifier, stateKey = owner) {
        Text(stringResource(R.string.firewall_intro))
        if (owner?.capabilities?.contains(ServerCapabilities.FIREWALL) != true) { Text(stringResource(R.string.error_capability_missing)); return@WorkspaceColumn }
        PageActionRow(refresh = {
            TextButton(enabled = !state.busy, onClick = { navigate { draft = null; defaults = null; model.refresh() } }) { ActionLabel(R.string.common_refresh) }
        })
        RefreshProgressIndicator(visible = visible && state.busy)
        OperationMessageDialog(state.problem?.takeIf { visible && !state.busy && !editing }?.let { firewallProblem(it) },
            eventKey = owner to state.feedbackVersion)
        if (visible && section != "records" && state.pending.isNotEmpty()) TextButton(onClick = { navigate { draft = null; defaults = null; section = "records" } }) { Text(stringResource(R.string.workspace_records_attention)) }
        if (visible && section == "records") state.pending.forEach { pending ->
            ManagementCard {
            Text(stringResource(R.string.firewall_pending), color = MaterialTheme.colorScheme.error)
            OutlinedButton(enabled = !state.busy, onClick = { model.accept(pending) }) { Text(stringResource(R.string.firewall_accept_facts)) }
            }
        }
        if (visible && section == "records" && state.pending.isEmpty()) ManagementCard { Text(stringResource(R.string.workspace_records_empty)) }
        WorkspaceSection(section == "overview") {
        if (facts == null) Text(stringResource(R.string.firewall_unverified)) else {
            ManagementCard {
            Surface(shape = MaterialTheme.shapes.medium, color = when {
                !facts.status.isAvailable -> MaterialTheme.colorScheme.errorContainer
                facts.status.isEnabled -> MaterialTheme.colorScheme.primaryContainer
                else -> MaterialTheme.colorScheme.surfaceVariant
            }) {
                Text(stringResource(if (!facts.status.isAvailable) R.string.firewall_unavailable else if (facts.status.backend == "windows-defender") R.string.firewall_windows_ready else if (facts.status.isEnabled) R.string.firewall_enabled else R.string.firewall_disabled),
                    Modifier.padding(horizontal = Spacing.md, vertical = Spacing.sm), style = MaterialTheme.typography.titleMedium)
            }
            Text(listOfNotNull(facts.status.backend.uppercase(), facts.status.version).joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(stringResource(R.string.firewall_policy_title), style = MaterialTheme.typography.titleSmall)
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                Column(Modifier.weight(1f)) {
                    Text(stringResource(R.string.firewall_incoming), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text(firewallValue(facts.status.defaultIncomingPolicy))
                }
                Column(Modifier.weight(1f)) {
                    Text(stringResource(R.string.firewall_outgoing), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text(firewallValue(facts.status.defaultOutgoingPolicy))
                }
            }
            if (facts.status.backend == "windows-defender") Text(stringResource(R.string.firewall_windows_scope))
            state.checkedAtMillis?.let { Text(stringResource(R.string.operations_checked, DateFormat.getDateTimeInstance().format(Date(it)))) }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                OutlinedButton(enabled = ready && !editing, onClick = { model.requestChange(requireNotNull(owner), facts, FirewallChange(FirewallChangeKind.Enabled, enabled = if (facts.status.backend == "windows-defender") true else !facts.status.isEnabled)) }) {
                    Text(stringResource(if (facts.status.backend != "windows-defender" && facts.status.isEnabled) R.string.firewall_disable else R.string.firewall_enable))
                }
                if (facts.status.backend == "windows-defender") OutlinedButton(enabled = ready && !editing, onClick = {
                    model.requestChange(requireNotNull(owner), facts, FirewallChange(FirewallChangeKind.Enabled, enabled = false))
                }) { Text(stringResource(R.string.firewall_disable)) }
                OutlinedButton(enabled = ready && !editing, onClick = {
                    initialDefaults = (facts.status.defaultIncomingPolicy ?: "deny") to (facts.status.defaultOutgoingPolicy ?: "allow")
                    defaults = initialDefaults
                }) { Text(stringResource(R.string.firewall_defaults)) }
                OutlinedButton(enabled = ready && !editing, onClick = {
                    selected = null
                    initialDraft = FirewallRule(0, "allow", "in", "tcp", "any", "any", "", "IPv4 + IPv6")
                    draft = initialDraft
                }) { Text(stringResource(R.string.firewall_create)) }
            }
            }
            if (defaults != null && confirmation == null && leave == null) AlertDialog(
                onDismissRequest = { if (!state.busy) navigate { defaults = null } }, modifier = Modifier.imePadding(),
                title = { Text(stringResource(R.string.firewall_defaults)) },
                text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                FirewallChoices(stringResource(R.string.firewall_incoming), if (facts.status.backend == "windows-defender") listOf("allow", "deny") else FirewallValues.policies, defaults!!.first, ready) { defaults = it to defaults!!.second }
                FirewallChoices(stringResource(R.string.firewall_outgoing), if (facts.status.backend == "windows-defender") listOf("allow", "deny") else FirewallValues.policies, defaults!!.second, ready) { defaults = defaults!!.first to it }
                FirewallEditorFeedback(state.problem, state.busy)
                if (!ready) TextButton(enabled = !state.busy, onClick = model::refresh) { Text(stringResource(R.string.firewall_refresh_keep_draft)) }
                } },
                confirmButton = { Button(enabled = ready, onClick = { model.requestChange(requireNotNull(owner), facts, FirewallChange(FirewallChangeKind.Defaults, incoming = defaults!!.first, outgoing = defaults!!.second)) }) { ActionLabel(R.string.common_save) } },
                dismissButton = { TextButton(enabled = !state.busy, onClick = { navigate { defaults = null } }) { Text(stringResource(R.string.common_cancel)) } })
            Box(Modifier.fillMaxWidth()) {
                Row(horizontalArrangement = Arrangement.spacedBy(Spacing.md)) {
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        if (facts.rules.isEmpty()) Text(stringResource(R.string.firewall_empty))
                        facts.rules.forEach { rule ->
                            ManagementCard {
                            ListRow(title = "${rule.number} · ${firewallValue(rule.action)} · ${firewallValue(rule.direction)}", subtitle = "${rule.source} → ${rule.destination}:${rule.port}",
                                supporting = "${firewallValue(rule.protocol)} · ${rule.addressFamily}", selected = selected == rule.number,
                                onClick = { navigate { selected = rule.number; draft = null; defaults = null } })
                            }
                        }
                    }
                    val rule = draft
                    if (rule != null && confirmation == null && leave == null) AlertDialog(
                        onDismissRequest = { if (!state.busy) navigate { draft = null } }, modifier = Modifier.imePadding(),
                        title = { Text(stringResource(if (rule.number == 0) R.string.firewall_create else R.string.firewall_edit)) },
                        text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        FirewallChoices(stringResource(R.string.firewall_action), if (facts.status.backend == "windows-defender") listOf("allow", "deny") else FirewallValues.actions, rule.action, ready) { draft = rule.copy(action = it) }
                        FirewallChoices(stringResource(R.string.firewall_direction), FirewallValues.directions, rule.direction, ready) { draft = rule.copy(direction = it) }
                        FirewallChoices(stringResource(R.string.firewall_protocol), FirewallValues.protocols, rule.protocol, ready) { draft = rule.copy(protocol = it) }
                        OutlinedTextField(rule.source, { draft = rule.copy(source = it) }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.firewall_source)) }, singleLine = true, enabled = ready)
                        OutlinedTextField(rule.destination, { draft = rule.copy(destination = it) }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.firewall_destination)) }, singleLine = true, enabled = ready)
                        OutlinedTextField(rule.port, { draft = rule.copy(port = it) }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.firewall_port)) }, singleLine = true, enabled = ready)
                        Text(stringResource(R.string.firewall_rule_help), style = MaterialTheme.typography.bodySmall)
                        FirewallEditorFeedback(state.problem, state.busy)
                        if (!ready) TextButton(enabled = !state.busy, onClick = model::refresh) { Text(stringResource(R.string.firewall_refresh_keep_draft)) }
                        } }, confirmButton = {
                        val change = FirewallChange(if (rule.number == 0) FirewallChangeKind.Create else FirewallChangeKind.Replace, rule.number.takeIf { it > 0 }, rule = rule)
                        Button(enabled = ready && runCatching { change.validate() }.isSuccess, onClick = { model.requestChange(requireNotNull(owner), facts, change) }) { ActionLabel(R.string.common_save) }
                        }, dismissButton = { TextButton(enabled = !state.busy, onClick = { navigate { draft = null } }) { Text(stringResource(R.string.common_cancel)) } })
                }
            }
            facts.rules.firstOrNull { it.number == selected }?.let { rule ->
                if (!editing) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    OutlinedButton(enabled = ready, onClick = { initialDraft = rule; draft = rule }) { Text(stringResource(R.string.firewall_edit)) }
                    OutlinedButton(enabled = ready, onClick = { model.requestChange(requireNotNull(owner), facts, FirewallChange(FirewallChangeKind.Delete, rule.number)) }) { ActionLabel(R.string.common_delete) }
                }
            }
        }
    }
    }
    val pending = confirmation
    if (visible && facts == null && editing && pending == null && leave == null) FirewallRetainedDraftDialog(
        draft, defaults, state.busy, state.problem, model::refresh,
        { navigate { draft = null; defaults = null } })
    if (pending != null) key(pending) { AlertDialog(onDismissRequest = { model.dismissConfirmation(pending) }, title = { Text(stringResource(R.string.firewall_confirm)) },
        text = { Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.firewall_management_warning))
            Text(firewallChangeLabel(pending.change.kind))
            val reviewedRule = pending.change.rule ?: pending.expected.rules.singleOrNull {
                pending.change.kind == FirewallChangeKind.Delete && it.number == pending.change.number
            }
            reviewedRule?.let {
                Text("${firewallValue(it.action)} · ${firewallValue(it.direction)} · ${it.addressFamily}")
                FirewallReviewField(stringResource(R.string.firewall_source_label), it.source)
                FirewallReviewField(stringResource(R.string.firewall_destination_label), it.destination)
                FirewallReviewField(stringResource(R.string.firewall_port_label), it.port.ifEmpty { firewallValue("any") })
                FirewallReviewField(stringResource(R.string.firewall_protocol), firewallValue(it.protocol))
            }
            pending.change.number?.let { Text(stringResource(R.string.firewall_number, it)) }
            pending.change.enabled?.let { Text(stringResource(if (it) R.string.firewall_enable else R.string.firewall_disable)) }
            pending.change.incoming?.let { Text(stringResource(R.string.firewall_defaults_value, firewallValue(it), firewallValue(pending.change.outgoing))) }
        } }, confirmButton = { Button(enabled = ready, onClick = {
            model.change(pending)
        }) { Text(stringResource(R.string.firewall_submit)) } }, dismissButton = { TextButton(onClick = { model.dismissConfirmation(pending) }) { Text(stringResource(R.string.common_cancel)) } }) }
    if (leave != null) AlertDialog(onDismissRequest = { leave = null }, title = { Text(stringResource(R.string.firewall_discard)) },
        text = { Text(stringResource(R.string.firewall_discard_help)) }, confirmButton = { TextButton(onClick = { val action = leave; leave = null; action?.invoke() }) { Text(stringResource(R.string.firewall_discard_action)) } },
        dismissButton = { TextButton(onClick = { leave = null }) { Text(stringResource(R.string.firewall_continue_editing)) } })
}
@Composable private fun FirewallReviewField(label: String, value: String) {
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}
@OptIn(ExperimentalLayoutApi::class)
@Composable private fun FirewallChoices(label: String, values: List<String>, selected: String, enabled: Boolean, onSelect: (String) -> Unit) {
    Text(label)
    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) { values.forEach { value -> FilterChip(selected = value == selected, enabled = enabled, onClick = { onSelect(value) }, label = { Text(firewallValue(value)) }) } }
}
@Composable private fun FirewallEditorFeedback(problem: String?, busy: Boolean) {
    if (!busy) problem?.let { Text(firewallProblem(it), color = MaterialTheme.colorScheme.error) }
    RefreshProgressIndicator(visible = busy)
}

@Composable internal fun FirewallRetainedDraftDialog(rule: FirewallRule?, defaults: Pair<String, String>?,
    busy: Boolean, problem: String?, refresh: () -> Unit, close: () -> Unit) {
    AlertDialog(onDismissRequest = { if (!busy) close() }, modifier = Modifier.imePadding(),
        title = { Text(stringResource(if (rule != null) R.string.firewall_edit else R.string.firewall_defaults)) },
        text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.firewall_unverified))
            rule?.let { Text("${firewallValue(it.action)} · ${firewallValue(it.direction)} · ${firewallValue(it.protocol)}"); Text("${it.source} → ${it.destination}:${it.port}") }
            defaults?.let { Text(stringResource(R.string.firewall_defaults_value, firewallValue(it.first), firewallValue(it.second))) }
            FirewallEditorFeedback(problem, busy)
        } }, confirmButton = { TextButton(enabled = !busy, onClick = refresh) { Text(stringResource(R.string.firewall_refresh_keep_draft)) } },
        dismissButton = { TextButton(enabled = !busy, onClick = close) { Text(stringResource(R.string.common_cancel)) } })
}
@Composable internal fun firewallChangeLabel(kind: FirewallChangeKind) = stringResource(when (kind) {
    FirewallChangeKind.Enabled -> R.string.firewall_enabled_change; FirewallChangeKind.Defaults -> R.string.firewall_defaults
    FirewallChangeKind.Create -> R.string.firewall_create; FirewallChangeKind.Replace -> R.string.firewall_edit; FirewallChangeKind.Delete -> R.string.common_delete
})
@Composable private fun firewallValue(value: String?): String = when (value) {
    "allow" -> stringResource(R.string.firewall_allow); "deny" -> stringResource(R.string.firewall_deny); "reject" -> stringResource(R.string.firewall_reject)
    "limit" -> stringResource(R.string.firewall_limit); "in" -> stringResource(R.string.firewall_incoming); "out" -> stringResource(R.string.firewall_outgoing)
    "any" -> stringResource(R.string.firewall_any); "tcp" -> "TCP"; "udp" -> "UDP"; else -> stringResource(R.string.firewall_unknown_value)
}
@Composable private fun firewallProblem(code: String) = stringResource(when (code) {
    "firewall.refresh_failed" -> R.string.firewall_refresh_failed
    "firewall.recovery_storage_failed" -> R.string.firewall_recovery_storage_failed
    "firewall.facts_changed" -> R.string.firewall_conflict
    "firewall.ufw_not_installed", "firewall.not_supported", "firewall.unsupported_platform" -> R.string.firewall_unavailable
    "elevation-required", "firewall.elevation_required", "firewall.permission_denied", "firewall.privileged_proxy_required" -> R.string.firewall_permission
    else -> R.string.firewall_unverified
})
