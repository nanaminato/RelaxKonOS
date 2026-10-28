package app.relaxkonos.mobile.ui.manage.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ComponentChange
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.core.net.ComponentStateView
import app.relaxkonos.mobile.data.ComponentRead
import app.relaxkonos.mobile.ui.common.ActionLabel
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.PageActionRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.nav.Routes
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Component lifecycle for this server: state, install, update, uninstall and the current Nginx
 * selection. Changes are frozen by the host component planner and applied through the owning
 * domain's installation ledger, so approval, elevation, idempotency and recovery are shared with the
 * application screens. Mihomo and sing-box keep their runtime pages, which own their plan formats.
 */
@Composable
fun ComponentCenterScreen(
    owner: SessionState.Active,
    onBack: () -> Unit,
    onOpenRoute: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val container = appContainer()
    val model: ComponentCenterViewModel = viewModel(factory = viewModelFactory {
        initializer { ComponentCenterViewModel(container.session, container.components) }
    })
    ComponentCenterContent(owner, model, onBack, onOpenRoute, modifier)
}

@Composable
internal fun ComponentCenterContent(
    owner: SessionState.Active,
    model: ComponentCenterViewModel,
    onBack: () -> Unit,
    onOpenRoute: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val container = appContainer()
    val state by model.state.collectAsStateWithLifecycle()
    var selected by remember(owner) { mutableStateOf<ComponentRead?>(null) }
    LaunchedEffect(owner) { model.load(owner) }

    selected?.let { read ->
        val definition = ComponentCatalog.definition(read.componentId)
        AlertDialog(
            onDismissRequest = { selected = null },
            title = { Text(stringResource(definition?.title ?: R.string.components_title)) },
            text = {
                Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    ComponentFacts(read)
                }
            },
            confirmButton = { TextButton(onClick = { selected = null }) { Text(stringResource(R.string.common_close)) } },
        )
    }
    state.review?.let { review ->
        ChangeDialog(review, state.busy,
            onConfirm = { model.confirmChange(owner, container.elevationAnswers) },
            onDismiss = model::dismissReview)
    }
    state.operation?.let { notice ->
        AlertDialog(
            onDismissRequest = model::dismissOperation,
            title = { Text(stringResource(R.string.components_operation_title)) },
            text = { Text(stringResource(R.string.components_operation_submitted, notice.operationId)) },
            confirmButton = {
                TextButton(onClick = { model.dismissOperation(); onOpenRoute(Routes.MANAGE_OPERATIONS) }) {
                    Text(stringResource(R.string.components_recovery_open))
                }
            },
            dismissButton = { TextButton(onClick = model::dismissOperation) { Text(stringResource(R.string.common_close)) } },
        )
    }
    state.nginx?.let { choice ->
        NginxSelectionDialog(choice, state.busy, onSelect = { model.selectNginx(owner, it) }, onDismiss = model::closeNginxSelection)
    }
    state.actionProblem?.let { problem ->
        AlertDialog(
            onDismissRequest = model::dismissActionProblem,
            title = { Text(stringResource(R.string.components_action_failed)) },
            text = { Text(problem) },
            confirmButton = { TextButton(onClick = model::dismissActionProblem) { Text(stringResource(R.string.common_close)) } },
        )
    }

    Column(modifier.fillMaxSize().padding(Spacing.lg), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(title = stringResource(R.string.components_title), onBack = onBack)
        Text(stringResource(R.string.components_note), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        PageActionRow(
            refresh = {
                OutlinedButton(enabled = !state.loading, onClick = { model.refresh(owner) }) {
                    ActionLabel(R.string.common_refresh)
                }
            },
            actions = {
                if (state.recoveryVisible) {
                    TextButton(onClick = { onOpenRoute(Routes.MANAGE_OPERATIONS) }) {
                        Text(stringResource(R.string.components_recovery_open))
                    }
                }
            },
        )
        when {
            !state.loaded || (state.loading && state.rows.isEmpty()) -> Box(Modifier.fillMaxWidth().padding(Spacing.xl), Alignment.Center) {
                CircularProgressIndicator()
            }
            state.error -> EmptyState(
                text = stringResource(R.string.components_error),
                icon = DesktopIcons.applications,
                action = { TextButton(onClick = { model.load(owner) }) { Text(stringResource(R.string.common_retry)) } },
            )
            state.rows.isEmpty() -> EmptyState(text = stringResource(R.string.components_empty), icon = DesktopIcons.applications)
            else -> Column(Modifier.weight(1f).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                state.rows.forEach { read ->
                    ComponentCard(
                        read = read,
                        busy = state.busy,
                        canManage = owner.controller && capabilityFor(read.componentId, owner.capabilities),
                        nginxLoading = state.nginxLoading,
                        onChange = { change -> model.requestChange(owner, read.componentId, change) },
                        onNginx = { model.openNginxSelection(owner) },
                        onOpen = onOpenRoute,
                        onDetails = { selected = read },
                    )
                }
            }
        }
    }
}

@Composable
private fun ComponentCard(
    read: ComponentRead,
    busy: Boolean,
    canManage: Boolean,
    nginxLoading: Boolean,
    onChange: (ComponentChange) -> Unit,
    onNginx: () -> Unit,
    onOpen: (String) -> Unit,
    onDetails: () -> Unit,
) {
    val definition = ComponentCatalog.definition(read.componentId)
    val view = read.view
    val state = view?.state
    // Package-backed components freeze install/update/uninstall. A managed archive (Windows Nginx,
    // frpc/frps) is installed from an explicit version/file or its runtime page; this screen does not
    // pretend it can pick a release the host has not offered.
    val packageBacked = canManage && read.componentId in ComponentCatalog.changeableIds && state != null &&
        state.mode == "systemPackage" && view.problems.isEmpty()
    val installable = packageBacked && state?.ownership == "absent"
    // Updating or removing an external installation would claim ownership the host did not grant.
    val manageable = packageBacked && state?.ownership == "managed" && view?.recoveryRequired != true
    SectionCard(
        title = stringResource(definition?.title ?: R.string.components_title),
        subtitle = definition?.let { stringResource(it.description) },
        leading = definition?.icon ?: DesktopIcons.applications,
        trailing = {
            Text(stringResource(ownershipLabel(view)), style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        },
    ) {
        if (view == null) {
            Text(stringResource(R.string.components_unavailable_reason, read.problem ?: "component.unavailable"),
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
        } else {
            ComponentSummary(view)
        }
        if (installable || manageable) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
                if (installable) {
                    TextButton(enabled = !busy, onClick = { onChange(ComponentChange.Install) }) {
                        Text(stringResource(R.string.components_action_install))
                    }
                }
                if (manageable) {
                    TextButton(enabled = !busy, onClick = { onChange(ComponentChange.Update) }) {
                        Text(stringResource(R.string.components_action_update))
                    }
                    TextButton(enabled = !busy, onClick = { onChange(ComponentChange.Uninstall) }) {
                        Text(stringResource(R.string.components_action_uninstall))
                    }
                }
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onDetails) { Text(stringResource(R.string.components_details)) }
            if (read.componentId == "nginx" && canManage) {
                TextButton(enabled = !busy && !nginxLoading, onClick = onNginx) {
                    Text(stringResource(R.string.components_nginx_select))
                }
            }
            if (view?.recoveryRequired == true) {
                TextButton(onClick = { onOpen(Routes.MANAGE_OPERATIONS) }) {
                    Text(stringResource(R.string.components_recovery_open))
                }
            }
            val route = definition?.route
            if (route != null) {
                TextButton(onClick = { onOpen(route) }) { Text(stringResource(R.string.components_open_app)) }
            }
        }
    }
}

@Composable
private fun ChangeDialog(
    review: ComponentChangeReview,
    busy: Boolean,
    onConfirm: () -> Unit,
    onDismiss: () -> Unit,
) {
    val definition = ComponentCatalog.definition(review.componentId)
    val action = stringResource(changeLabel(review.change))
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(stringResource(R.string.components_change_title, action)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.components_change_target, stringResource(definition?.title ?: R.string.components_title)))
                if (review.change == ComponentChange.Uninstall) {
                    Text(stringResource(R.string.components_change_keep_data), style = MaterialTheme.typography.bodySmall)
                }
                if (review.plan.notes.isNotEmpty()) {
                    Text(review.plan.notes.joinToString(", "), style = MaterialTheme.typography.bodySmall)
                }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy && review.plan.allowed, onClick = onConfirm) { Text(action) }
        },
        dismissButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } },
    )
}

@Composable
private fun NginxSelectionDialog(
    choice: NginxChoiceState,
    busy: Boolean,
    onSelect: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(stringResource(R.string.components_nginx_select)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                if (choice.servers.isEmpty()) {
                    Text(stringResource(R.string.components_nginx_empty))
                } else {
                    choice.servers.forEach { server ->
                        val current = server.id == choice.current
                        TextButton(enabled = !busy && !current, onClick = { onSelect(server.id) }, modifier = Modifier.fillMaxWidth()) {
                            Column(Modifier.fillMaxWidth()) {
                                Text(if (current) "✓ ${server.id}" else server.id)
                                Text(
                                    listOfNotNull(server.managementMode, server.version).joinToString(" · "),
                                    style = MaterialTheme.typography.bodySmall,
                                    maxLines = 1, overflow = TextOverflow.Ellipsis,
                                )
                            }
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text(stringResource(R.string.common_close)) } },
    )
}

@Composable
private fun ComponentSummary(view: ComponentStateView) {
    val state = view.state
    if (state == null) {
        Text(stringResource(if (view.unsupported) R.string.components_unsupported else R.string.components_state_unavailable),
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (view.problems.isNotEmpty()) {
            Text(view.problems.joinToString(", "), style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2, overflow = TextOverflow.Ellipsis)
        }
        return
    }
    Text(stringResource(R.string.components_version,
        state.activeVersion ?: stringResource(R.string.components_version_unknown)),
        style = MaterialTheme.typography.bodyMedium)
    Text(stringResource(R.string.components_mode, stringResource(modeLabel(state.mode))),
        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    if (view.recoveryRequired) {
        Text(stringResource(R.string.components_recovery), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.error)
    }
}

@Composable
private fun ComponentFacts(read: ComponentRead) {
    val view = read.view
    if (view == null) {
        Text(stringResource(R.string.components_unavailable_reason, read.problem ?: "component.unavailable"))
        return
    }
    val state = view.state
    if (state == null) {
        Text(stringResource(if (view.unsupported) R.string.components_unsupported else R.string.components_state_unavailable))
        if (view.problems.isNotEmpty()) Text(view.problems.joinToString(", "))
        return
    }
    Fact(R.string.components_ownership, stringResource(ownershipLabel(view)))
    Fact(R.string.components_version_label, state.activeVersion ?: stringResource(R.string.components_version_unknown))
    Fact(R.string.components_mode_label, stringResource(modeLabel(state.mode)))
    Fact(R.string.components_target, state.target)
    Fact(R.string.components_host, state.hostId)
    Fact(R.string.components_revision, state.revision.toString())
    if (view.recoveryRequired) Text(stringResource(R.string.components_recovery), color = MaterialTheme.colorScheme.error)
    if (view.problems.isNotEmpty()) Fact(R.string.components_problems, view.problems.joinToString(", "))
    val pending = view.pendingOperationId
    if (pending != null) Fact(R.string.components_pending_operation, pending)
}

@Composable
private fun Fact(label: Int, value: String) {
    Column {
        Text(stringResource(label), style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodySmall)
    }
}

private fun ownershipLabel(view: ComponentStateView?): Int = when (view?.ownership) {
    "managed" -> R.string.components_state_managed
    "external" -> R.string.components_state_external
    "absent" -> R.string.components_state_absent
    else -> R.string.components_state_unknown
}

private fun capabilityFor(componentId: String, capabilities: Set<String>): Boolean = when (componentId) {
    "nginx" -> ServerCapabilities.WEB_SERVER in capabilities
    "docker" -> ServerCapabilities.DOCKER in capabilities
    "samba" -> ServerCapabilities.FILE_SERVICES in capabilities
    "squid" -> ServerCapabilities.GATEWAY_PROXY in capabilities
    "frpc", "frps" -> ServerCapabilities.TUNNELS in capabilities
    else -> false
}

private fun modeLabel(mode: String): Int = when (mode) {
    "systemPackage" -> R.string.components_mode_system_package
    else -> R.string.components_mode_managed_archive
}

private fun changeLabel(change: ComponentChange): Int = when (change) {
    ComponentChange.Install -> R.string.components_action_install
    ComponentChange.Update -> R.string.components_action_update
    ComponentChange.Uninstall -> R.string.components_action_uninstall
}
