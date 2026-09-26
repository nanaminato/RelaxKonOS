package app.relaxkonos.mobile.ui.manage.deployments

import android.app.Application
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.DeploymentBrowser
import app.relaxkonos.mobile.data.DeploymentBrowserState
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing
import java.text.DateFormat
import java.util.Date

class DeploymentsViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    val browser = DeploymentBrowser(container.deployments, container.session, viewModelScope)
}

@Composable
fun DeploymentsScreen(
    layoutState: LayoutState,
    showDetail: Boolean,
    onOpenDetail: () -> Unit,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val viewModel: DeploymentsViewModel = viewModel()
    val browser = viewModel.browser
    val state by browser.state.collectAsState()
    val expanded = layoutState == LayoutState.Expanded
    val available = state.owner?.capabilities?.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS) == true
    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(
            title = stringResource(R.string.deployments_title),
            subtitle = state.owner?.let { "${it.userName} · ${it.workspaceName}" },
            onBack = onBack,
            trailing = {
                TextButton(onClick = browser::refresh, enabled = available && !state.loading && !state.detailLoading) {
                    Text(stringResource(R.string.common_refresh))
                }
            },
        )
        if (!available) {
            EmptyHint(stringResource(R.string.error_capability_missing))
            return@Column
        }
        if (expanded) {
            Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(Spacing.lg)) {
                DeploymentList(state, { browser.select(it) }, Modifier.weight(1f))
                DeploymentDetail(state, Modifier.weight(1.2f))
            }
        } else if (showDetail) {
            DeploymentDetail(state, Modifier.weight(1f))
        } else {
            DeploymentList(state, { browser.select(it); onOpenDetail() }, Modifier.weight(1f))
        }
    }
}

@Composable
private fun DeploymentList(state: DeploymentBrowserState, onSelect: (String) -> Unit, modifier: Modifier) {
    LazyColumn(modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        item { Text(stringResource(R.string.deployments_read_only), style = MaterialTheme.typography.bodySmall) }
        item {
            SectionCard(title = stringResource(R.string.deployments_runtime)) {
                when (val runtime = state.runtime) {
                    is ApiResult.Success -> {
                        if (runtime.value.isAvailable) {
                            Text(stringResource(R.string.deployments_runtime_ready))
                            Text(listOfNotNull(runtime.value.serverVersion, runtime.value.operatingSystem, runtime.value.architecture).joinToString(" · "))
                        } else {
                            Text(deploymentProblem(runtime.value.problemCode).text(), color = MaterialTheme.colorScheme.error)
                        }
                    }
                    null -> Text(stringResource(if (ServerCapabilities.DOCKER !in state.owner!!.capabilities)
                        R.string.deployments_runtime_missing else R.string.common_loading))
                    else -> Text(runtime.runtimeFailure().text(), color = MaterialTheme.colorScheme.error)
                }
            }
        }
        state.owner?.executionEligibility?.takeIf { !it.available }?.let { eligibility ->
            item { ExecutionEligibilityNotice(eligibility.reason) }
        }
        if (state.loading) item { LinearProgressIndicator(Modifier.fillMaxWidth()) }
        state.checkedAtMillis?.let { item { CheckedAt(it) } }
        when (val result = state.applications) {
            is ApiResult.Success -> {
                if (result.value.isEmpty()) item { EmptyHint(stringResource(R.string.deployments_empty)) }
                items(result.value, key = { it.id }) { application ->
                    SectionGroup {
                        ListRow(
                            title = application.name,
                            subtitle = stringResource(R.string.deployments_actual, label(application.actualState)),
                            supporting = stringResource(R.string.deployments_revision, revisionLabel(application.currentRevisionNumber)),
                            selected = state.selectedId == application.id,
                            onClick = { onSelect(application.id) },
                        )
                    }
                }
            }
            null -> Unit
            else -> item { Text(result.deploymentFailure().text(), color = MaterialTheme.colorScheme.error) }
        }
    }
}

@Composable
private fun DeploymentDetail(state: DeploymentBrowserState, modifier: Modifier) {
    LazyColumn(modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        if (state.selectedId == null) item { EmptyHint(stringResource(R.string.deployments_select)) }
        if (state.detailLoading) item { LinearProgressIndicator(Modifier.fillMaxWidth()) }
        state.detailCheckedAtMillis?.let { item { CheckedAt(it) } }
        when (val result = state.detail) {
            is ApiResult.Success -> {
                val snapshot = result.value
                val app = snapshot.application
                item {
                    SectionCard(title = app.name, subtitle = stringResource(R.string.deployments_overview)) {
                        Text(stringResource(R.string.deployments_actual, label(app.actualState)))
                        Text(stringResource(R.string.deployments_desired, label(app.desiredState)))
                        Text(stringResource(R.string.deployments_revision, revisionLabel(app.currentRevisionNumber)))
                        Text(stringResource(R.string.deployments_source, label(app.sourceKind)))
                        Text(stringResource(R.string.deployments_workload, label(app.workloadKind)))
                        Text(stringResource(R.string.deployments_readiness, label(app.readinessLevel)))
                        app.containerName?.let { Text(stringResource(R.string.deployments_container, it)) }
                        Text(stringResource(R.string.deployments_ports,
                            app.hostPort?.let { "${app.bindAddress}:$it" } ?: stringResource(R.string.deployments_unpublished), app.containerPort))
                        app.domain?.let { Text(stringResource(R.string.deployments_domain, it)) }
                        if (app.driftProblemCode != null) Text(stringResource(R.string.deployments_drift), color = MaterialTheme.colorScheme.error)
                    }
                }
                snapshot.activeOperation?.let { operation ->
                    item { OperationCard(operation, stringResource(R.string.deployments_active)) }
                }
                item { Text(stringResource(R.string.deployments_operations), style = MaterialTheme.typography.titleMedium) }
                if (snapshot.operations.isEmpty()) item { EmptyHint(stringResource(R.string.deployments_no_operations)) }
                items(snapshot.operations.filter { it.operationId != snapshot.activeOperation?.operationId }, key = { it.operationId }) {
                    OperationCard(it, label(it.kind))
                }
                item { Text(stringResource(R.string.deployments_revisions), style = MaterialTheme.typography.titleMedium) }
                if (snapshot.revisions.isEmpty()) item { EmptyHint(stringResource(R.string.deployments_no_revision)) }
                items(snapshot.revisions, key = { it.id }) { revision ->
                    SectionCard(title = stringResource(R.string.deployments_revision_number, revision.number)) {
                        Text(revision.imageReference)
                        if (revision.isCurrent) Text(stringResource(R.string.deployments_current))
                    }
                }
            }
            null -> Unit
            else -> item { Text(result.deploymentFailure().text(), color = MaterialTheme.colorScheme.error) }
        }
    }
}

@Composable
private fun OperationCard(operation: DeploymentOperation, title: String) {
    SectionCard(title = title) {
        Text(stringResource(R.string.deployments_operation_state, label(operation.state)))
        Text(stringResource(R.string.deployments_stage, label(operation.stage)))
        Text(stringResource(R.string.deployments_operation_id, operation.operationId), style = MaterialTheme.typography.bodySmall)
        operation.problemCode?.let { Text(deploymentProblem(it).text(), color = MaterialTheme.colorScheme.error) }
        if (operation.recoveryProblemCode != null) Text(stringResource(R.string.deployments_recovery), color = MaterialTheme.colorScheme.error)
    }
}

@Composable
private fun label(value: String): String = stringResource(deploymentLabel(value))

@Composable
private fun revisionLabel(number: Int?): String = number?.toString() ?: stringResource(R.string.deployments_no_revision)

@Composable
private fun CheckedAt(millis: Long) {
    Text(stringResource(R.string.deployments_checked_at, DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.MEDIUM).format(Date(millis))),
        style = MaterialTheme.typography.bodySmall)
}
