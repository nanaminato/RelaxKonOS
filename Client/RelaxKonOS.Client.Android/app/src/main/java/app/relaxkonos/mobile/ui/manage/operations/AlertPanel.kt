package app.relaxkonos.mobile.ui.manage.operations

import app.relaxkonos.mobile.ui.common.ActionLabel
import app.relaxkonos.mobile.ui.common.ActivityIndicator
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import app.relaxkonos.mobile.ui.common.StatusTone
import android.Manifest
import android.app.Application
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.app.NotificationManagerCompat
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

internal class AlertViewModel(application: Application) : AndroidViewModel(application) {
    val browser = EventAlertBrowser(getApplication<RelaxKonApplication>().container.eventAlerts, viewModelScope)
    override fun onCleared() { browser.stop(); super.onCleared() }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun AlertPanel(owner: SessionState.Active, onOpenTarget: (OperationTarget) -> Unit) {
    val model: AlertViewModel = viewModel()
    val browser = model.browser
    val state by browser.state.collectAsState()
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var resumed by remember(lifecycle) { mutableStateOf(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
    DisposableEffect(lifecycle, browser) {
        val observer = LifecycleEventObserver { _, _ -> resumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); browser.stop() }
    }
    LaunchedEffect(owner, resumed) { if (resumed) browser.activate(owner) else browser.stop() }
    var confirmation by remember(owner, resumed) { mutableStateOf<Pair<OperationalAlert, AlertMutation>?>(null) }
    val visible = state.owner === owner && state.active
    HorizontalDivider()
    Text(stringResource(R.string.operations_alerts), style = MaterialTheme.typography.titleMedium)
    Text(stringResource(R.string.event_center_observation), style = MaterialTheme.typography.bodySmall)
    AlertNotificationSettings(owner)
    if (!visible) return
    val summary = (state.summary as? ApiResult.Success)?.value
    summary?.let {
        Text(stringResource(R.string.event_center_summary, it.openCount, it.acknowledgedCount, it.unacknowledgedCriticalCount))
        it.updatedAtMillis?.let { time -> Text(stringResource(R.string.event_center_summary_time, eventTime(time))) }
    }
    if (state.summary != null && state.summary !is ApiResult.Success) AlertFailure(state.summary)
    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        FilterChip(selected = !state.eventsMode, onClick = { browser.filters(false, state.alertQuery, state.eventQuery) },
            enabled = !state.actionBusy, label = { Text(stringResource(R.string.operations_alerts)) })
        FilterChip(selected = state.eventsMode, onClick = { browser.filters(true, state.alertQuery, state.eventQuery) },
            enabled = !state.actionBusy, label = { Text(stringResource(R.string.event_center_events)) })
    }
    if (!state.eventsMode) {
        Text(stringResource(R.string.event_center_status))
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            FilterChip(selected = state.alertQuery.status == null, enabled = !state.actionBusy,
                onClick = { browser.filters(false, state.alertQuery.copy(status = null), state.eventQuery) }, label = { Text(stringResource(R.string.event_center_all)) })
            AlertStatusFilter.entries.forEach { status -> FilterChip(selected = state.alertQuery.status == status,
                enabled = !state.actionBusy, onClick = { browser.filters(false, state.alertQuery.copy(status = status), state.eventQuery) }, label = { Text(alertStatus(status.wire)) }) }
        }
    }
    Text(stringResource(R.string.event_center_severity))
    val severity = if (state.eventsMode) state.eventQuery.severity else state.alertQuery.severity
    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        (listOf<EventSeverityFilter?>(null) + EventSeverityFilter.entries).forEach { value ->
            FilterChip(selected = severity == value, enabled = !state.actionBusy, onClick = {
                browser.filters(state.eventsMode, state.alertQuery.copy(severity = value), state.eventQuery.copy(severity = value))
            }, label = { Text(if (value == null) stringResource(R.string.event_center_all) else alertSeverity(value.wire)) })
        }
    }
    if (state.eventsMode) {
        Text(stringResource(R.string.event_center_source))
        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            (listOf<EventSourceFilter?>(null) + EventSourceFilter.entries).forEach { source -> FilterChip(
                selected = state.eventQuery.source == source, enabled = !state.actionBusy,
                onClick = { browser.filters(true, state.alertQuery, state.eventQuery.copy(source = source)) },
                label = { Text(if (source == null) stringResource(R.string.event_center_all) else eventSource(source.wire)) }) }
        }
        var type by remember(owner, state.eventQuery.type) { mutableStateOf(state.eventQuery.type.orEmpty()) }
        OutlinedTextField(value = type, onValueChange = { if (it.length <= 128) type = it }, modifier = Modifier.fillMaxWidth(),
            enabled = !state.actionBusy, singleLine = true, label = { Text(stringResource(R.string.event_center_type)) })
        TextButton(enabled = !state.actionBusy, onClick = { browser.filters(true, state.alertQuery, state.eventQuery.copy(type = type.trim().ifEmpty { null })) }) {
            Text(stringResource(R.string.event_center_apply))
        }
    }
    TextButton(onClick = browser::refresh, enabled = !state.loading && !state.actionBusy) { ActionLabel(R.string.common_refresh) }
    if (state.loading) ActivityIndicator(stringResource(R.string.event_center_loading))
    state.checkedAtMillis?.let { Text(stringResource(R.string.event_center_checked, eventTime(it)), style = MaterialTheme.typography.bodySmall) }
    if (state.readResult != null && state.readResult !is ApiResult.Success) AlertFailure(state.readResult)
    if (state.readResult is ApiResult.Success && (if (state.eventsMode) state.events.isEmpty() else state.alerts.isEmpty())) Text(stringResource(R.string.operations_alerts_empty))
    if (state.eventsMode) state.events.forEach { EventRow(owner, it, onOpenTarget) }
    else state.alerts.forEach { alert -> ListRow(title = alertTitle(alert.type), subtitle = alert.problemCode,
        supporting = stringResource(R.string.operations_alert_summary, alertSeverity(alert.severity), alertStatus(alert.status), alert.occurrenceCount),
        selected = alert.id == state.selectedId, onClick = { browser.select(alert.id) }) }
    if (state.atLimit) Text(stringResource(R.string.event_center_limit))
    else if (state.nextCursor != null) TextButton(onClick = browser::more, enabled = !state.loading && !state.actionBusy) { Text(stringResource(R.string.event_center_more)) }
    if (!state.eventsMode && state.selectedId != null) {
        HorizontalDivider()
        Text(stringResource(R.string.operations_alert_detail), style = MaterialTheme.typography.titleSmall)
        if (state.detailLoading) ActivityIndicator(stringResource(R.string.event_center_loading))
        if (state.detail != null && state.detail !is ApiResult.Success) AlertFailure(state.detail)
        val detail = (state.detail as? ApiResult.Success)?.value
        detail?.let { value ->
            val alert = value.alert
            Text(alertTitle(alert.type)); Text(alert.id, style = MaterialTheme.typography.bodySmall)
            Text(stringResource(R.string.operations_alert_summary, alertSeverity(alert.severity), alertStatus(alert.status), alert.occurrenceCount))
            Text(alert.problemCode)
            Text(stringResource(R.string.event_center_first_last, eventTime(alert.firstOccurredAtMillis), eventTime(alert.lastOccurredAtMillis)))
            Text(stringResource(R.string.event_center_last_event, alert.lastEventId), style = MaterialTheme.typography.bodySmall)
            alert.acknowledgedAtMillis?.let { Text(stringResource(R.string.event_center_acknowledged, eventTime(it), alert.acknowledgedByReference.orEmpty())) }
            alert.resolutionReason?.let { Text(stringResource(R.string.event_center_reason_value, it)) }
            OperationDestinations.alert(owner, alert)?.let { target -> OutlinedButton(onClick = { onOpenTarget(target) }) { Text(stringResource(R.string.operations_open_target)) } }
            Text(stringResource(R.string.event_center_history), style = MaterialTheme.typography.titleSmall)
            value.events.forEach { EventRow(owner, it, onOpenTarget) }
            Text(stringResource(R.string.event_center_actions), style = MaterialTheme.typography.titleSmall)
            value.actions.forEach { action ->
                Text(stringResource(R.string.event_center_action_record, actionKind(action.kind), eventTime(action.createdAtMillis), action.actorReference.orEmpty(), action.note.orEmpty()), style = MaterialTheme.typography.bodySmall)
            }
            if (state.unknown) OperationMessageDialog(if (state.actionBusy) null else stringResource(R.string.event_center_unknown), tone = StatusTone.Warning)
            else if (state.actionResult != null && state.actionResult !is ApiResult.Success) AlertFailure(state.actionResult)
            if (state.actionResult is ApiResult.Success) Text(stringResource(R.string.event_center_action_saved))
            TextButton(enabled = !state.actionBusy && !state.detailLoading, onClick = browser::reconcile) { Text(stringResource(R.string.event_center_reconcile)) }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                AlertMutation.entries.filter { canMutateAlert(alert, it) }.forEach { action -> OutlinedButton(
                    enabled = !state.actionBusy && !state.unknown && !state.detailLoading,
                    onClick = { confirmation = alert to action }) { Text(alertAction(action)) } }
            }
        }
    }
    confirmation?.let { (baseline, action) ->
        AlertActionDialog(baseline, action, !state.actionBusy && !state.unknown && !state.detailLoading &&
            (state.detail as? ApiResult.Success)?.value?.alert == baseline,
            onDismiss = { confirmation = null }, onSubmit = { reason, expiry -> browser.mutate(baseline, action, reason, expiry); confirmation = null })
    }
}

@Composable
private fun EventRow(owner: SessionState.Active, event: OperationalEvent, onOpenTarget: (OperationTarget) -> Unit) {
    Column(Modifier.fillMaxWidth().padding(vertical = Spacing.sm), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        Text(alertTitle(event.type), style = MaterialTheme.typography.titleSmall)
        Text(stringResource(R.string.event_center_event_summary, eventTime(event.occurredAtMillis), alertSeverity(event.severity), eventSource(event.source), alertOutcome(event.outcome)))
        Text(event.problemCode)
        Text(stringResource(R.string.event_center_event_reference, event.resourceType, event.resourceReference, event.correlationId, event.operationId.orEmpty()), style = MaterialTheme.typography.bodySmall)
        event.evidence?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
        OperationDestinations.event(owner, event)?.let { target -> TextButton(onClick = { onOpenTarget(target) }) { Text(stringResource(R.string.operations_open_target)) } }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun AlertActionDialog(baseline: OperationalAlert, action: AlertMutation, ready: Boolean, onDismiss: () -> Unit, onSubmit: (String?, Long?) -> Unit) {
    var reason by remember(baseline, action) { mutableStateOf("") }
    var duration by remember(baseline, action) { mutableStateOf(10) }
    val needsReason = action == AlertMutation.Resolve || action == AlertMutation.Suppress
    AlertDialog(onDismissRequest = onDismiss, title = { Text(alertAction(action)) }, text = {
        Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()).imePadding(), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.event_center_confirm, alertTitle(baseline.type), alertStatus(baseline.status), baseline.occurrenceCount))
            Text(stringResource(R.string.event_center_action_note))
            if (action != AlertMutation.RemoveSuppression) OutlinedTextField(value = reason, onValueChange = { if (it.length <= 512) reason = it },
                modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(if (needsReason) R.string.event_center_reason else R.string.event_center_optional_note)) })
            if (action == AlertMutation.Suppress) FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                listOf(10, 30, 60).forEach { minutes -> FilterChip(selected = minutes == duration, onClick = { duration = minutes }, label = { Text(stringResource(R.string.event_center_minutes, minutes)) }) }
            }
            if (!ready) Text(stringResource(R.string.event_center_changed), color = MaterialTheme.colorScheme.error)
        }
    }, confirmButton = { TextButton(enabled = ready && (!needsReason || reason.isNotBlank()), onClick = {
        onSubmit(reason.trim().ifEmpty { null }, if (action == AlertMutation.Suppress) System.currentTimeMillis() + duration * 60_000L else null)
    }) { Text(alertAction(action)) } }, dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } })
}

@Composable
private fun AlertFailure(result: ApiResult<*>?) {
    val key = when (result) {
        is ApiResult.Problem -> when {
            result.status == 403 -> R.string.event_center_forbidden
            result.status == 404 -> R.string.event_center_not_found
            result.code == "event-alerts.invalid_transition" -> R.string.event_center_changed
            result.status == 400 -> R.string.event_center_invalid
            else -> R.string.operations_alerts_unavailable
        }
        else -> R.string.operations_alerts_unavailable
    }
    OperationMessageDialog(stringResource(key), eventKey = result)
}

private fun eventTime(value: Long?): String = value?.let { DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT).format(Date(it)) }.orEmpty()
@Composable
private fun alertAction(action: AlertMutation): String = stringResource(when (action) {
    AlertMutation.Acknowledge -> R.string.operations_acknowledge
    AlertMutation.Resolve -> R.string.event_center_resolve
    AlertMutation.Suppress -> R.string.event_center_suppress
    AlertMutation.RemoveSuppression -> R.string.event_center_unsuppress
})
@Composable
private fun actionKind(kind: String): String = when (kind.lowercase()) {
    "acknowledged" -> alertAction(AlertMutation.Acknowledge)
    "resolved" -> alertAction(AlertMutation.Resolve)
    "suppressed" -> alertAction(AlertMutation.Suppress)
    "suppression-removed" -> alertAction(AlertMutation.RemoveSuppression)
    else -> kind
}
@Composable
private fun eventSource(source: String): String {
    val resource = when (source) {
    "deployment" -> R.string.event_center_deployment
    "certificate" -> R.string.event_center_certificate
    "guardian" -> R.string.event_center_guardian
    "docker" -> R.string.event_center_docker
    "tunnel" -> R.string.event_center_tunnel
    "eventCenter" -> R.string.event_center_internal
    else -> return source
    }
    return stringResource(resource)
}

@Composable
private fun AlertNotificationSettings(owner: SessionState.Active) {
    val context = LocalContext.current
    val container = (context.applicationContext as RelaxKonApplication).container
    val store = container.alertNotificationStore
    var enabled by remember(owner.serviceId) {
        mutableStateOf(AlertNotificationCategory.entries.filter { store.enabled(owner.serviceId, it) }.toSet())
    }
    var policyError by remember(owner.serviceId) { mutableStateOf(false) }
    var permissionDenied by remember(owner.serviceId) { mutableStateOf(false) }
    val requestPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        permissionDenied = !it
    }
    Text(stringResource(R.string.alert_notifications_title), style = MaterialTheme.typography.titleSmall)
    AlertNotificationCategory.entries.forEach { category ->
        Row(Modifier.fillMaxWidth().padding(vertical = Spacing.xs), verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(alertCategoryLabel(category)), Modifier.weight(1f))
            Switch(checked = category in enabled, onCheckedChange = { checked ->
                try {
                    store.setEnabled(owner.serviceId, category, checked)
                    enabled = if (checked) enabled + category else enabled - category
                    policyError = false
                    if (!checked) container.foregroundAlertNotifier.clearForCategory(owner, category)
                    container.foregroundAlertNotifier.restart()
                    if (checked && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
                        context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                        requestPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                    }
                } catch (_: Exception) {
                    policyError = true
                }
            })
        }
    }
    Text(stringResource(R.string.alert_notifications_foreground_note), style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant)
    OperationMessageDialog(if (policyError) stringResource(R.string.alert_notifications_save_failed) else null, onDismiss = { policyError = false })
    val systemDisabled = permissionDenied || (enabled.isNotEmpty() &&
        !NotificationManagerCompat.from(context).areNotificationsEnabled())
    if (systemDisabled) Text(stringResource(R.string.alert_notifications_permission_off),
        color = MaterialTheme.colorScheme.error)
}

private fun alertCategoryLabel(category: AlertNotificationCategory): Int = when (category) {
    AlertNotificationCategory.Deployments -> R.string.alert_notifications_deployments
    AlertNotificationCategory.Certificates -> R.string.alert_notifications_certificates
    AlertNotificationCategory.Infrastructure -> R.string.alert_notifications_infrastructure
    AlertNotificationCategory.Guardian -> R.string.alert_notifications_guardian
}

@Composable
private fun alertTitle(type: String): String {
    val resource = when (type) {
        "deployment.operation_failed" -> R.string.alert_deployment_failed
        "backup.definition_failed" -> R.string.alert_backup_definition_failed
        "certificate.renewal_failed" -> R.string.alert_certificate_renewal_failed
        "certificate.renewal_exhausted" -> R.string.alert_certificate_renewal_exhausted
        "certificate.expiring_soon" -> R.string.alert_certificate_expiring
        "guardian.agent_unavailable" -> R.string.alert_guardian_unavailable
        "guardian.workload_failed" -> R.string.alert_guardian_workload_failed
        "guardian.server_restart_failed" -> R.string.alert_guardian_restart_failed
        "docker.engine_unavailable" -> R.string.alert_docker_unavailable
        "docker.operation_failed" -> R.string.alert_docker_operation_failed
        "tunnel.disconnected" -> R.string.alert_tunnel_disconnected
        "event-center.source_degraded" -> R.string.alert_source_degraded
        else -> null
    }
    return if (resource == null) type else stringResource(resource)
}

@Composable
private fun alertSeverity(value: String): String = when (value.lowercase()) {
    "warning" -> stringResource(R.string.alert_severity_warning)
    "error" -> stringResource(R.string.alert_severity_error)
    "critical" -> stringResource(R.string.alert_severity_critical)
    else -> value
}

@Composable
private fun alertStatus(value: String): String = when (value.lowercase()) {
    "open" -> stringResource(R.string.alert_status_open)
    "acknowledged" -> stringResource(R.string.alert_status_acknowledged)
    "resolved" -> stringResource(R.string.alert_status_resolved)
    "suppressed" -> stringResource(R.string.alert_status_suppressed)
    else -> value
}

@Composable
private fun alertOutcome(value: String): String = when (value.lowercase()) {
    "failed" -> stringResource(R.string.operations_failed)
    "recovered" -> stringResource(R.string.alert_outcome_recovered)
    else -> value
}
