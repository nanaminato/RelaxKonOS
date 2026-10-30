package app.relaxkonos.mobile.ui.manage.components

import androidx.compose.foundation.layout.Arrangement
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ComponentChange
import app.relaxkonos.mobile.core.net.ComponentPinnedArchive
import app.relaxkonos.mobile.core.net.InstallationFileReference
import app.relaxkonos.mobile.core.net.InstallationKind
import app.relaxkonos.mobile.core.net.InstallationService
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.core.net.ComponentStateView
import app.relaxkonos.mobile.data.ComponentRead
import app.relaxkonos.mobile.ui.common.ActionLabel
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.PageActionRow
import app.relaxkonos.mobile.ui.common.RemotePathField
import app.relaxkonos.mobile.ui.common.RemotePathKind
import app.relaxkonos.mobile.ui.common.rememberUsageOpenDocument
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.nav.Routes
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

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
        initializer {
            ComponentCenterViewModel(container.session, container.components,
                container.installations, container.elevationAnswers)
        }
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
    // A component whose archive is installed from a file already on the host; null closes the dialog.
    var fileInstall by remember(owner) { mutableStateOf<String?>(null) }
    // FRP maintenance: an upgrade asks for a version, a rollback or uninstall only confirms.
    var frpUpgrade by remember(owner) { mutableStateOf<String?>(null) }
    var frpConfirm by remember(owner) { mutableStateOf<Pair<String, InstallationKind>?>(null) }
    var nginxUninstall by remember(owner) { mutableStateOf(false) }
    var tailscaleMaintenance by remember(owner) { mutableStateOf<Triple<String, Long, String>?>(null) }
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
    tailscaleMaintenance?.let { review ->
        val title = when (review.first) {
            "ConfigureOfficialSource" -> R.string.components_tailscale_source_setup
            "RefreshSources" -> R.string.components_tailscale_refresh
            "Stop" -> R.string.components_tailscale_stop
            else -> R.string.components_tailscale_close
        }
        AlertDialog(
            onDismissRequest = { if (!state.busy) tailscaleMaintenance = null },
            title = { Text(stringResource(title)) },
            text = { Column(Modifier.heightIn(max = 440.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(review.third)
                Text(stringResource(when (review.first) {
                    "ConfigureOfficialSource" -> R.string.components_tailscale_source_confirm
                    "RefreshSources" -> R.string.components_tailscale_refresh_confirm
                    else -> R.string.components_tailscale_recovery_confirm
                }))
            } },
            confirmButton = { TextButton(enabled = !state.busy, onClick = {
                model.maintainTailscale(owner, review.first, review.second); tailscaleMaintenance = null
            }) { Text(stringResource(title)) } },
            dismissButton = { TextButton(enabled = !state.busy, onClick = { tailscaleMaintenance = null }) { Text(stringResource(R.string.common_cancel)) } },
        )
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
    fileInstall?.let { componentId ->
        ArchiveFileInstallDialog(
            componentId = componentId,
            busy = state.busy,
            onDismiss = { fileInstall = null },
            onSubmit = { version, referenceId ->
                fileInstall = null
                model.installArchiveFromFile(owner, componentId, version, referenceId)
            },
        )
    }
    frpUpgrade?.let { componentId ->
        FrpUpgradeDialog(
            busy = state.busy,
            onDismiss = { frpUpgrade = null },
            onSubmit = { version ->
                frpUpgrade = null
                model.maintainFrp(owner, componentId, InstallationKind.Upgrade, version)
            },
        )
    }
    frpConfirm?.let { (componentId, kind) ->
        val rollback = kind == InstallationKind.Repair
        AlertDialog(
            onDismissRequest = { if (!state.busy) frpConfirm = null },
            title = {
                Text(stringResource(if (rollback) R.string.components_frp_rollback_title
                    else R.string.components_frp_uninstall_title))
            },
            text = {
                Text(stringResource(if (rollback) R.string.components_frp_rollback_message
                    else R.string.components_frp_uninstall_message))
            },
            confirmButton = {
                TextButton(enabled = !state.busy, onClick = {
                    frpConfirm = null
                    model.maintainFrp(owner, componentId, kind, rollback = rollback)
                }) { Text(stringResource(if (rollback) R.string.components_frp_rollback
                    else R.string.components_action_uninstall)) }
            },
            dismissButton = { TextButton(enabled = !state.busy, onClick = { frpConfirm = null }) { Text(stringResource(R.string.common_cancel)) } },
        )
    }
    if (nginxUninstall) {
        AlertDialog(
            onDismissRequest = { if (!state.busy) nginxUninstall = false },
            title = { Text(stringResource(R.string.components_nginx_uninstall_title)) },
            text = { Text(stringResource(R.string.components_nginx_uninstall_message)) },
            confirmButton = {
                TextButton(enabled = !state.busy, onClick = {
                    nginxUninstall = false
                    model.uninstallNginx(owner, "nginx")
                }) { Text(stringResource(R.string.components_action_uninstall)) }
            },
            dismissButton = { TextButton(enabled = !state.busy, onClick = { nginxUninstall = false }) { Text(stringResource(R.string.common_cancel)) } },
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
                        onChange = { change, pin -> model.requestChange(owner, read.componentId, change, pin = pin) },
                        onMaintenance = { action -> read.view?.state?.let { tailscaleMaintenance = Triple(action, it.revision, it.hostId) } },
                        onInstallFromFile = { fileInstall = read.componentId },
                        onFrpMaintenance = { kind ->
                            when (kind) {
                                InstallationKind.Upgrade -> frpUpgrade = read.componentId
                                else -> frpConfirm = read.componentId to kind
                            }
                        },
                        onNginxUninstall = { nginxUninstall = true },
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
    onChange: (ComponentChange, ComponentPinnedArchive?) -> Unit,
    onMaintenance: (String) -> Unit,
    onInstallFromFile: () -> Unit,
    onFrpMaintenance: (InstallationKind) -> Unit,
    onNginxUninstall: () -> Unit,
    onNginx: () -> Unit,
    onOpen: (String) -> Unit,
    onDetails: () -> Unit,
) {
    val definition = ComponentCatalog.definition(read.componentId)
    val view = read.view
    val state = view?.state
    // Package-backed components freeze install/update/uninstall. A managed archive the host offers as
    // pinned releases (frpc/frps) is installed from one explicitly chosen version; other archives are
    // observed and navigated only, because this build refuses their execution.
    val packageBacked = canManage && read.componentId in ComponentCatalog.changeableIds && state != null &&
        state.mode in setOf("systemPackage", "nativeInstaller") && view.problems.isEmpty() && !view.recoveryRequired
    val installable = packageBacked && state?.ownership == "absent"
    val archiveInstallable = canManage && read.componentId in ComponentCatalog.archiveInstallableIds && state != null &&
        state.mode == "managedArchive" && state.ownership == "absent" && view.problems.isEmpty() &&
        view.pinnedArchives.isNotEmpty() && !view.recoveryRequired
    val archiveObservedOnly = canManage && read.componentId in ComponentCatalog.changeableIds && state != null &&
        state.ownership == "absent" && state.mode == "managedArchive" && view.problems.isEmpty() &&
        read.componentId !in ComponentCatalog.archiveInstallableIds &&
        read.componentId !in ComponentCatalog.fileInstallableIds
    // A file reference installs the same domain service from an archive the operator supplies.
    val fileInstallable = canManage && read.componentId in ComponentCatalog.fileInstallableIds && state != null &&
        state.ownership == "absent" && view.problems.isEmpty() && !view.recoveryRequired
    // FRP maintenance is the one lifecycle the center owns for an installed archive pair.
    val frpMaintenance = canManage && read.componentId in setOf("frpc", "frps") && state?.ownership == "managed" &&
        view.problems.isEmpty() && !view.recoveryRequired
    // Nginx removal uses the same domain ledger the application used, from the single component entry.
    val nginxUninstallable = canManage && read.componentId == "nginx" && state?.ownership == "managed" &&
        view.problems.isEmpty() && !view.recoveryRequired
    // An external package may be updated or removed only after the host verified its identity; this
    // never grants long-term management, which still requires an explicit desktop registration.
    val manageable = packageBacked && view?.recoveryRequired != true &&
        (state?.ownership == "managed" || state?.ownership == "external" && view?.installedPackage != null)
    var pin by remember(read.componentId, view?.pinRevision) {
        mutableStateOf(view?.pinnedArchives?.firstOrNull())
    }
    var pinDialog by remember(read.componentId) { mutableStateOf(false) }
    if (pinDialog && view != null) {
        ArchiveVersionDialog(
            pins = view.pinnedArchives,
            selected = pin,
            busy = busy,
            onSelect = { pin = it; pinDialog = false },
            onDismiss = { pinDialog = false },
        )
    }
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
            if (view.externalPackageChange) Text(stringResource(R.string.components_external_package_change,
                view.recordedPackageVersion ?: "—", view.version ?: "—"), style = MaterialTheme.typography.bodySmall)
        }
        if (canManage && read.componentId == "tailscale" && view != null && state?.mode == "systemPackage") {
            val setup = "component.host_policy_required" in view.problems
            TextButton(enabled = !busy, onClick = { onMaintenance(if (setup) "ConfigureOfficialSource" else "RefreshSources") }) {
                Text(stringResource(if (setup) R.string.components_tailscale_source_setup else R.string.components_tailscale_refresh))
            }
        }
        if (canManage && read.componentId == "tailscale" && view?.recoveryRequired == true && view.pendingOperationId != null) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                TextButton(enabled = !busy, onClick = { onMaintenance("Stop") }) { Text(stringResource(R.string.components_tailscale_stop)) }
                TextButton(enabled = !busy, onClick = { onMaintenance("Close") }) { Text(stringResource(R.string.components_tailscale_close)) }
            }
        }
        if (installable || manageable) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
                if (installable) {
                    TextButton(enabled = !busy, onClick = { onChange(ComponentChange.Install, null) }) {
                        Text(stringResource(R.string.components_action_install))
                    }
                }
                if (manageable) {
                    TextButton(enabled = !busy, onClick = { onChange(ComponentChange.Update, null) }) {
                        Text(stringResource(R.string.components_action_update))
                    }
                    TextButton(enabled = !busy, onClick = { onChange(ComponentChange.Uninstall, null) }) {
                        Text(stringResource(R.string.components_action_uninstall))
                    }
                }
            }
        }
        if (archiveInstallable) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
                OutlinedButton(enabled = !busy, onClick = { pinDialog = true }) {
                    Text(stringResource(R.string.components_archive_pick))
                }
                Text(pin?.version ?: stringResource(R.string.components_archive_unavailable),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                TextButton(enabled = !busy && pin != null, onClick = { pin?.let { onChange(ComponentChange.Install, it) } }) {
                    Text(stringResource(R.string.components_action_install))
                }
            }
        }
        if (fileInstallable) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
                TextButton(enabled = !busy, onClick = onInstallFromFile) {
                    Text(stringResource(R.string.components_archive_file_title))
                }
            }
        }
        if (frpMaintenance) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
                TextButton(enabled = !busy, onClick = { onFrpMaintenance(InstallationKind.Upgrade) }) {
                    Text(stringResource(R.string.components_action_update))
                }
                TextButton(enabled = !busy, onClick = { onFrpMaintenance(InstallationKind.Repair) }) {
                    Text(stringResource(R.string.components_frp_rollback))
                }
                TextButton(enabled = !busy, onClick = { onFrpMaintenance(InstallationKind.Uninstall) }) {
                    Text(stringResource(R.string.components_action_uninstall))
                }
            }
        }
        if (nginxUninstallable) {
            Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
                TextButton(enabled = !busy, onClick = onNginxUninstall) {
                    Text(stringResource(R.string.components_action_uninstall))
                }
            }
        }
        if (archiveObservedOnly) {
            Text(stringResource(R.string.components_archive_not_executable), style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
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
private fun FrpUpgradeDialog(busy: Boolean, onDismiss: () -> Unit, onSubmit: (String) -> Unit) {
    var version by remember { mutableStateOf("") }
    val valid = version.trim().matches(Regex("[A-Za-z0-9][A-Za-z0-9._-]{0,31}"))
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(stringResource(R.string.components_frp_upgrade_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.components_frp_upgrade_message), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                OutlinedTextField(version, { version = it }, label = { Text(stringResource(R.string.components_frp_version)) },
                    singleLine = true, enabled = !busy, modifier = Modifier.fillMaxWidth())
            }
        },
        confirmButton = {
            TextButton(enabled = !busy && valid, onClick = { onSubmit(version.trim()) }) {
                Text(stringResource(R.string.components_action_update))
            }
        },
        dismissButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } },
    )
}

@Composable
private fun ArchiveFileInstallDialog(
    componentId: String,
    busy: Boolean,
    onDismiss: () -> Unit,
    onSubmit: (version: String, referenceId: String) -> Unit,
) {
    val container = appContainer()
    val owner = container.activeSession
    val service = fileInstallService(componentId) ?: return
    val scope = rememberCoroutineScope()
    var version by remember { mutableStateOf("") }
    var path by remember { mutableStateOf("") }
    var reference by remember { mutableStateOf<InstallationFileReference?>(null) }
    var creating by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }
    val validVersion = version.trim().matches(Regex("[A-Za-z0-9][A-Za-z0-9._-]{0,31}"))
    fun clearReference() { reference = null; problem = null }
    val localPicker = rememberLauncherForActivityResult(
        rememberUsageOpenDocument("ComponentCenter.archive")) { uri ->
        val active = owner
        if (uri == null || active == null || busy || creating) return@rememberLauncherForActivityResult
        val document = container.uploadDocuments.open(uri.toString())
        if (document == null) { problem = "installation.file_unavailable"; return@rememberLauncherForActivityResult }
        creating = true; problem = null
        scope.launch {
            val result = try {
                container.installations.upload(active, service, document)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                ApiResult.Transport(null)
            }
            if (container.activeSession !== active) return@launch
            when (result) {
                is ApiResult.Success -> reference = result.value
                is ApiResult.Problem -> problem = result.code ?: "installation.failed"
                is ApiResult.Transport -> problem = "installation.unreachable"
            }
            creating = false
        }
    }
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(stringResource(R.string.components_archive_file_title)) },
        text = {
            Column(Modifier.heightIn(max = 440.dp).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.components_archive_file_note), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                androidx.compose.material3.OutlinedTextField(version, { version = it; clearReference() },
                    label = { Text(stringResource(R.string.components_archive_file_version)) },
                    singleLine = true, enabled = !busy, modifier = Modifier.fillMaxWidth())
                RemotePathField(path, { path = it; clearReference() }, R.string.components_archive_file_path,
                    RemotePathKind.File, enabled = !busy)
                OutlinedButton(enabled = !busy && owner != null && path.isNotBlank() && !creating, onClick = {
                    val active = owner ?: return@OutlinedButton
                    creating = true; problem = null
                    scope.launch {
                        val result = try {
                            container.installations.fileReference(active, service, path.trim())
                        } catch (cancelled: CancellationException) {
                            throw cancelled
                        } catch (_: Exception) {
                            ApiResult.Transport(null)
                        }
                        if (container.activeSession !== active) return@launch
                        when (result) {
                            is ApiResult.Success -> reference = result.value
                            is ApiResult.Problem -> problem = result.code ?: "installation.failed"
                            is ApiResult.Transport -> problem = "installation.unreachable"
                        }
                        creating = false
                    }
                }) {
                    Text(stringResource(if (reference == null) R.string.components_archive_file_reference
                        else R.string.components_archive_file_reference_ready))
                }
                reference?.let { ref ->
                    Text(stringResource(R.string.components_archive_file_reference_note, ref.fileName),
                        style = MaterialTheme.typography.bodySmall)
                }
                OutlinedButton(enabled = !busy && owner != null && !creating, onClick = {
                    localPicker.launch(arrayOf("application/zip", "application/gzip",
                        "application/x-gzip", "application/octet-stream"))
                }) {
                    Text(stringResource(R.string.components_archive_file_local))
                }
                problem?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy && validVersion && reference?.expired() == false,
                onClick = { reference?.let { onSubmit(version.trim(), it.id) } }) {
                Text(stringResource(R.string.components_action_install))
            }
        },
        dismissButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text(stringResource(R.string.common_cancel)) } },
    )
}

@Composable
private fun ArchiveVersionDialog(
    pins: List<ComponentPinnedArchive>,
    selected: ComponentPinnedArchive?,
    busy: Boolean,
    onSelect: (ComponentPinnedArchive) -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(stringResource(R.string.components_archive_pick)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Text(stringResource(R.string.components_archive_note), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                pins.forEach { pin ->
                    val current = pin.releaseId == selected?.releaseId
                    TextButton(enabled = !busy, onClick = { onSelect(pin) }, modifier = Modifier.fillMaxWidth()) {
                        Column(Modifier.fillMaxWidth()) {
                            Text(if (current) "✓ ${pin.version}" else pin.version)
                            Text("${pin.format} · ${pin.rid} · SHA-256 ${pin.sha256.take(12)}…",
                                style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text(stringResource(R.string.common_close)) } },
    )
}

@Composable
internal fun ChangeDialog(
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
            Column(Modifier.heightIn(max = 440.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                Text(stringResource(R.string.components_change_target, stringResource(definition?.title ?: R.string.components_title)))
                review.plan.hostId?.let { Text("${stringResource(R.string.components_host)}: $it") }
                review.targetVersion?.let {
                    Text(stringResource(R.string.components_archive_candidate, it), style = MaterialTheme.typography.bodySmall)
                }
                review.plan.source?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                review.plan.sha256?.let { Text("SHA-256 $it", style = MaterialTheme.typography.bodySmall) }
                if (review.componentId == "tailscale") Text(stringResource(R.string.components_tailscale_change_warning))
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

internal fun ownershipLabel(view: ComponentStateView?): Int = when (view?.ownership) {
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
    "tailscale" -> ServerCapabilities.TAILSCALE in capabilities
    "frpc", "frps" -> ServerCapabilities.TUNNELS in capabilities
    else -> false
}

private fun modeLabel(mode: String): Int = when (mode) {
    "systemPackage" -> R.string.components_mode_system_package
    "nativeInstaller" -> R.string.components_mode_native_installer
    else -> R.string.components_mode_managed_archive
}

private fun changeLabel(change: ComponentChange): Int = when (change) {
    ComponentChange.Install -> R.string.components_action_install
    ComponentChange.Update -> R.string.components_action_update
    ComponentChange.Uninstall -> R.string.components_action_uninstall
}
