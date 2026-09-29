package app.relaxkonos.mobile.ui.manage.operations

import android.Manifest
import android.app.Application
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.core.app.NotificationManagerCompat
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.OperationalAlert
import app.relaxkonos.mobile.core.net.OperationalAlertDetail
import app.relaxkonos.mobile.data.AlertNotificationCategory
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

private data class AlertState(
    val owner: SessionState.Active? = null,
    val loading: Boolean = false,
    val alerts: List<OperationalAlert> = emptyList(),
    val nextCursor: String? = null,
    val selectedId: String? = null,
    val detail: OperationalAlertDetail? = null,
    val error: Boolean = false,
    val actionError: Boolean = false,
)

private class AlertViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    var state by mutableStateOf(AlertState())
        private set
    private var generation = 0

    fun refresh(owner: SessionState.Active) {
        val request = ++generation
        state = AlertState(owner = owner, loading = true)
        viewModelScope.launch {
            try {
                val result = container.eventAlerts.page(owner)
                if (!current(owner, request)) return@launch
                state = when (result) {
                    is ApiResult.Success -> state.copy(loading = false, alerts = result.value.items, nextCursor = result.value.nextCursor)
                    else -> state.copy(loading = false, error = true)
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (current(owner, request)) state = state.copy(loading = false, error = true)
            }
        }
    }

    fun more(owner: SessionState.Active) {
        val cursor = state.nextCursor ?: return
        if (state.owner !== owner || state.loading) return
        val request = generation
        state = state.copy(loading = true)
        viewModelScope.launch {
            val result = container.eventAlerts.page(owner, cursor)
            if (!current(owner, request)) return@launch
            state = when (result) {
                is ApiResult.Success -> state.copy(loading = false,
                    alerts = (state.alerts + result.value.items).distinctBy { it.id }, nextCursor = result.value.nextCursor)
                else -> state.copy(loading = false, error = true)
            }
        }
    }

    fun select(owner: SessionState.Active, id: String) {
        if (state.owner !== owner) return
        val request = generation
        state = state.copy(selectedId = id, detail = null, actionError = false)
        viewModelScope.launch {
            val result = container.eventAlerts.detail(owner, id)
            if (current(owner, request) && state.selectedId == id) {
                state = state.copy(detail = (result as? ApiResult.Success)?.value,
                    actionError = result !is ApiResult.Success)
            }
        }
    }

    fun acknowledge(owner: SessionState.Active, id: String) {
        if (state.owner !== owner || state.selectedId != id) return
        val request = generation
        viewModelScope.launch {
            val result = container.eventAlerts.acknowledge(owner, id)
            if (!current(owner, request)) return@launch
            if (result is ApiResult.Success) {
                state = state.copy(alerts = state.alerts.map { if (it.id == id) result.value else it }, actionError = false)
                select(owner, id)
            } else state = state.copy(actionError = true)
        }
    }

    private fun current(owner: SessionState.Active, request: Int) =
        state.owner === owner && container.session.state.value === owner && request == generation
}

@Composable
internal fun AlertPanel(owner: SessionState.Active, onOpenDeployment: (String) -> Unit) {
    val viewModel: AlertViewModel = viewModel()
    val state = viewModel.state
    LaunchedEffect(owner) { viewModel.refresh(owner) }
    val visible = state.owner === owner
    val selected = if (visible) state.alerts.firstOrNull { it.id == state.selectedId } else null

    HorizontalDivider()
    Text(stringResource(R.string.operations_alerts), style = MaterialTheme.typography.titleMedium)
    Text(stringResource(R.string.operations_alerts_note), style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant)
    AlertNotificationSettings(owner)
    TextButton(onClick = { viewModel.refresh(owner) }, enabled = visible && !state.loading) {
        Text(stringResource(R.string.common_refresh))
    }
    if (visible && state.error) Text(stringResource(R.string.operations_alerts_unavailable), color = MaterialTheme.colorScheme.error)
    if (visible && !state.loading && !state.error && state.alerts.isEmpty()) Text(stringResource(R.string.operations_alerts_empty))
    if (visible) state.alerts.forEach { alert ->
        ListRow(title = alertTitle(alert.type), subtitle = alert.problemCode,
            supporting = stringResource(R.string.operations_alert_summary,
                alertSeverity(alert.severity), alertStatus(alert.status), alert.occurrenceCount),
            selected = alert.id == state.selectedId,
            onClick = { viewModel.select(owner, alert.id) })
    }
    if (visible && state.nextCursor != null) TextButton(onClick = { viewModel.more(owner) }, enabled = !state.loading) {
        Text(stringResource(R.string.operations_more_alerts))
    }
    if (selected != null) {
        Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.operations_alert_detail), style = MaterialTheme.typography.titleSmall)
            Text(selected.problemCode)
            Text(stringResource(R.string.operations_alert_count, selected.occurrenceCount))
            state.detail?.events?.take(10)?.forEach { event ->
                Text(stringResource(R.string.operations_alert_event, alertOutcome(event.outcome), event.problemCode),
                    style = MaterialTheme.typography.bodySmall)
            }
            if (state.actionError) Text(stringResource(R.string.operations_alert_action_failed), color = MaterialTheme.colorScheme.error)
            if (selected.status.equals("open", true)) {
                OutlinedButton(onClick = { viewModel.acknowledge(owner, selected.id) }) {
                    Text(stringResource(R.string.operations_acknowledge))
                }
            }
            if (selected.targetKind in setOf("applicationDeployment", "applicationDeploymentOperation") &&
                selected.targetResourceId != null) {
                OutlinedButton(onClick = { onOpenDeployment(selected.targetResourceId) }) {
                    Text(stringResource(R.string.operations_open_target))
                }
            }
        }
    }
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
    if (policyError) Text(stringResource(R.string.alert_notifications_save_failed), color = MaterialTheme.colorScheme.error)
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
