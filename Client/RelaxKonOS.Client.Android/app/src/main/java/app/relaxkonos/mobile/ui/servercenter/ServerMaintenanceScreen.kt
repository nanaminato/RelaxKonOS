package app.relaxkonos.mobile.ui.servercenter

import androidx.annotation.DrawableRes
import androidx.annotation.StringRes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.PrimaryScrollableTabRow
import androidx.compose.material3.Surface
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.IsoInstant
import app.relaxkonos.mobile.servercenter.MaintenancePending
import app.relaxkonos.mobile.servercenter.ServerDeploymentKind
import app.relaxkonos.mobile.servercenter.ServerHostPlatform
import app.relaxkonos.mobile.servercenter.ServerHostProbe
import app.relaxkonos.mobile.servercenter.ServerHostSnapshot
import app.relaxkonos.mobile.servercenter.ServerHostTarget
import app.relaxkonos.mobile.servercenter.ServerInstallMode
import app.relaxkonos.mobile.servercenter.normalizeRepairCertificateIdentities
import app.relaxkonos.mobile.servercenter.wireName
import app.relaxkonos.mobile.ui.common.CheckboxOption
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.StatusChip
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.common.toneContainer
import app.relaxkonos.mobile.ui.common.toneContent
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Layout
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing
import java.net.URI

@Composable
internal fun ServerMaintenanceScreen(host: ServerHostTarget?, modifier: Modifier = Modifier) {
    val container = appContainer()
    val assets = LocalContext.current.applicationContext.assets
    val model: ServerMaintenanceViewModel = viewModel(key = "maintenance-${host?.hostId}", factory =
        viewModelFactory {
            initializer { ServerMaintenanceViewModel(RepositoryMaintenanceActions(container, assets), container.maintenancePendingJournal) }
        })
    ServerMaintenanceContent(host, model, modifier)
}

@Composable
internal fun ServerMaintenanceContent(host: ServerHostTarget?, model: ServerMaintenanceViewModel, modifier: Modifier = Modifier) {
    val state by model.state.collectAsStateWithLifecycle()
    var wizard by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var installing by remember(host?.hostId) { mutableStateOf(false) }
    var page by rememberSaveable(host?.hostId) { mutableIntStateOf(0) }
    val pageScroll = remember(page) { androidx.compose.foundation.ScrollState(0) }
    var purge by remember(host?.hostId) { mutableStateOf(false) }
    var removeComponents by remember(host?.hostId) { mutableStateOf(setOf<String>()) }
    var sudo by remember(host?.hostId) { mutableStateOf("") }
    var addFirewallRule by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var repairCertificate by rememberSaveable(host?.hostId) { mutableStateOf(false) }
    var certificateIdentities by rememberSaveable(host?.hostId) {
        mutableStateOf(listOf("localhost", "127.0.0.1", host?.sshHost.orEmpty()).filter(String::isNotBlank).distinct().joinToString(","))
    }
    var repairPackagePath by remember(host?.hostId) { mutableStateOf("") }
    val activity = androidx.activity.compose.LocalActivity.current
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    DisposableEffect(host?.hostId, model, lifecycle) {
        val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
            if (event == androidx.lifecycle.Lifecycle.Event.ON_PAUSE) { model.setReviewActive(false); sudo = "" }
            if (event == androidx.lifecycle.Lifecycle.Event.ON_RESUME) model.setReviewActive(true)
        }
        lifecycle.addObserver(observer)
        onDispose {
            lifecycle.removeObserver(observer); model.setReviewActive(false)
            if (activity?.isChangingConfigurations != true) model.clearConfiguration()
        }
    }
    DisposableEffect(host?.hostId, page, wizard, model) {
        model.clearReview(); model.invalidateConfiguration()
        onDispose { model.clearReview(); model.invalidateConfiguration() }
    }
    val identitiesValid = runCatching { normalizeRepairCertificateIdentities(certificateIdentities) }.isSuccess
    androidx.activity.compose.BackHandler(enabled = wizard || page != 0) {
        if (!installing && !state.busy) {
            if (wizard) wizard = false else page = 0
        }
    }
    LaunchedEffect(host?.hostId, wizard) { if (host != null && !wizard) model.check(host.hostId) }

    if (wizard) {
        Column(modifier.fillMaxSize().imePadding()) {
            TextButton(onClick = { wizard = false }, enabled = !installing) { Text(stringResource(R.string.common_back)) }
            DeploymentSetupScreen(host, Modifier.weight(1f), onBusyChanged = { installing = it })
        }
    } else {
        BoxWithConstraints(modifier.fillMaxSize().imePadding()) {
            val contentWidth = minOf(maxWidth, Layout.contentMaxWidth)
            Column(
                Modifier.width(contentWidth).fillMaxHeight().align(Alignment.TopCenter),
            ) {
                MaintenanceHeader(host, state.busy) { host?.let { model.check(it.hostId, sudo = sudo) } }
                PrimaryScrollableTabRow(
                    selectedTabIndex = page,
                    edgePadding = Spacing.xs,
                    containerColor = Color.Transparent,
                    contentColor = MaterialTheme.colorScheme.primary,
                ) {
                    listOf(
                        R.string.server_maintenance_overview,
                        R.string.server_maintenance_environment,
                        R.string.server_maintenance_actions,
                        R.string.server_maintenance_history,
                    ).forEachIndexed { index, title ->
                        Tab(
                            selected = page == index,
                            onClick = { page = index },
                            text = { Text(stringResource(title)) },
                        )
                    }
                }
                if (state.busy) {
                    Column(
                        Modifier.fillMaxWidth().padding(horizontal = Spacing.lg, vertical = Spacing.sm),
                        verticalArrangement = Arrangement.spacedBy(Spacing.xs),
                    ) {
                        LinearProgressIndicator(Modifier.fillMaxWidth())
                        Text(
                            stringResource(state.progress),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                Column(
                    Modifier.weight(1f).fillMaxWidth().verticalScroll(pageScroll)
                        .padding(horizontal = Spacing.lg, vertical = Spacing.md),
                    verticalArrangement = Arrangement.spacedBy(Spacing.md),
                ) {
                    MaintenanceBanners(state) { model.recover(it) }
                    val snapshot = state.snapshot
                    if (page == 3) {
                        if (host != null) {
                            ServerInstallRecoveryPanel(
                                host.hostId,
                                state.probe?.let {
                                    if (it.hostPlatform.equals("linux", true)) ServerHostPlatform.Linux else ServerHostPlatform.Windows
                                },
                            )
                        }
                    } else if (snapshot == null) {
                        if (!state.busy) {
                            EmptyState(
                                text = stringResource(R.string.server_maintenance_status_empty),
                                icon = DesktopIcons.notice,
                                action = {
                                    OutlinedButton(
                                        onClick = { host?.let { model.check(it.hostId, sudo = sudo) } },
                                        enabled = host != null,
                                    ) { Text(stringResource(R.string.server_maintenance_check)) }
                                },
                            )
                        }
                    } else {
                        when (page) {
                            0 -> MaintenanceOverview(
                                snapshot = snapshot,
                                busy = state.busy,
                                pending = state.pending != null,
                                installEnabled = state.probe?.runtimeIdentifier != null,
                                onActions = { page = 2 },
                                onEnvironment = { page = 1 },
                                onInstall = { if (model.state.value.pending == null && !model.state.value.busy) wizard = true },
                            )
                            1 -> MaintenanceEnvironment(
                                snapshot = snapshot,
                                probe = state.probe,
                                busy = state.busy,
                                onCheck = { host?.let { model.check(it.hostId, sudo = sudo) } },
                            )
                            2 -> MaintenanceActions(
                                snapshot = snapshot,
                                busy = state.busy,
                                pending = state.pending != null,
                                installEnabled = state.probe?.runtimeIdentifier != null,
                                sudo = sudo,
                                onSudoChange = { sudo = it },
                                onInstall = { if (model.state.value.pending == null && !model.state.value.busy) wizard = true },
                                onUpgrade = { if (model.state.value.pending == null && !model.state.value.busy) wizard = true },
                                onComponents = { host?.let { model.openConfiguration(it.hostId, componentsOnly = true) } },
                                onSettings = { host?.let { model.openConfiguration(it.hostId, componentsOnly = false) } },
                                onRepair = {
                                    addFirewallRule = false
                                    repairCertificate = false
                                    host?.let { model.requestReview(it.hostId, ServerDeploymentKind.Repair) }
                                },
                                onAddFirewall = {
                                    host?.let { model.firewall(it.hostId, removeRules = false, sudo = sudo) }; sudo = ""
                                },
                                onRemoveFirewall = {
                                    host?.let { model.firewall(it.hostId, removeRules = true, sudo = sudo) }; sudo = ""
                                },
                                onUninstall = {
                                    purge = false
                                    removeComponents = emptySet()
                                    host?.let { model.requestReview(it.hostId, ServerDeploymentKind.Uninstall) }
                                },
                            )
                        }
                    }
                }
            }
        }
    }

    OperationMessageDialog(state.error.takeUnless { state.busy }?.text(), eventKey = state.feedbackVersion)
    state.configuration?.let { editor ->
        val version = state.configurationVersion
        ServerConfigurationDialog(editor, state.busy, version,
            onDismiss = { if (model.dismissConfiguration(editor, version)) sudo = "" },
            onApply = { draft -> if (model.configure(editor, version, draft, sudo)) sudo = "" })
    }
    state.review?.takeIf { it.kind == ServerDeploymentKind.Uninstall }?.let { review ->
        val cancelUninstall = { if (model.dismissReview(review)) sudo = "" }
        AlertDialog(onDismissRequest = cancelUninstall, title = { Text(stringResource(R.string.server_maintenance_uninstall)) },
            text = {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    Text(host?.let { "${it.sshHost}:${it.sshPort}" }.orEmpty(), style = MaterialTheme.typography.bodySmall)
                    Text(review.snapshot.installationId.orEmpty(), style = MaterialTheme.typography.bodySmall)
                    Text(stringResource(R.string.server_maintenance_uninstall_note))
                    Text(stringResource(R.string.server_components_note))
                    listOf(
                        "smb" to R.string.server_remove_smb,
                        "nginx" to R.string.server_remove_nginx,
                        "frp" to R.string.server_remove_frp,
                        "mihomo" to R.string.server_remove_mihomo,
                    ).forEach { (component, label) ->
                        CheckboxOption(component in removeComponents, stringResource(label), !state.busy) { checked ->
                            removeComponents = if (checked) removeComponents + component else removeComponents - component
                            if (!checked) purge = false
                        }
                    }
                    CheckboxOption(purge, stringResource(R.string.server_maintenance_purge), !state.busy) {
                        purge = it
                        if (it) removeComponents = setOf("smb", "nginx", "frp", "mihomo")
                    }
                }
            },
            confirmButton = {
                TextButton(
                    enabled = host != null && !state.busy,
                    onClick = {
                        if (model.submitReview(
                                review,
                                purge,
                                sudo,
                                removeComponents = listOf("smb", "nginx", "frp", "mihomo")
                                    .filter { it in removeComponents }.joinToString(","),
                            )
                        ) sudo = ""
                    },
                ) { Text(stringResource(R.string.server_maintenance_uninstall)) }
            },
            dismissButton = { TextButton(onClick = cancelUninstall) { Text(stringResource(R.string.common_cancel)) } },
        )
    }
    state.review?.takeIf { it.kind == ServerDeploymentKind.Repair }?.let { review ->
        val cancelRepair = { if (model.dismissReview(review)) sudo = "" }
        AlertDialog(
            onDismissRequest = cancelRepair,
            title = { Text(stringResource(R.string.server_maintenance_repair)) },
            text = {
                Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                    Text(host?.let { "${it.sshHost}:${it.sshPort}" }.orEmpty(), style = MaterialTheme.typography.bodySmall)
                    Text(review.snapshot.installationId.orEmpty(), style = MaterialTheme.typography.bodySmall)
                    PasswordTextField(
                        sudo,
                        { sudo = it },
                        stringResource(R.string.server_maintenance_password_label),
                        enabled = !state.busy,
                        supportingText = { Text(stringResource(R.string.server_maintenance_password_note)) },
                    )
                    OutlinedTextField(
                        repairPackagePath,
                        { repairPackagePath = it },
                        enabled = !state.busy,
                        label = { Text(stringResource(R.string.server_configuration_repair_package)) },
                    )
                    val certificateRepairSupported = review.snapshot.mode == ServerInstallMode.LinuxSystem ||
                        review.snapshot.mode == ServerInstallMode.WindowsSystem
                    if (certificateRepairSupported) {
                        CheckboxOption(addFirewallRule, stringResource(R.string.server_install_firewall_choice), !state.busy) { addFirewallRule = it }
                        Text(stringResource(R.string.server_install_firewall_help), style = MaterialTheme.typography.bodySmall)
                        CheckboxOption(repairCertificate, stringResource(R.string.server_maintenance_repair_certificate), !state.busy) { repairCertificate = it }
                        if (repairCertificate) {
                            Text(stringResource(R.string.server_maintenance_repair_certificate_note), style = MaterialTheme.typography.bodySmall)
                            OutlinedTextField(
                                certificateIdentities,
                                { certificateIdentities = it },
                                label = { Text(stringResource(R.string.ssh_workspace_deploy_certificate_names)) },
                                modifier = Modifier.fillMaxWidth(),
                                enabled = !state.busy,
                                isError = !identitiesValid,
                            )
                            if (!identitiesValid) Text(stringResource(R.string.server_maintenance_repair_certificate_invalid), color = MaterialTheme.colorScheme.error)
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        val certificateRepairSupported = review.snapshot.mode == ServerInstallMode.LinuxSystem ||
                            review.snapshot.mode == ServerInstallMode.WindowsSystem
                        if (model.submitReview(
                                review,
                                sudo = sudo,
                                repairCertificate = repairCertificate && certificateRepairSupported,
                                certificateIdentities = certificateIdentities,
                                addFirewallRule = addFirewallRule && certificateRepairSupported,
                                repairPackagePath = repairPackagePath,
                            )
                        ) sudo = ""
                    },
                    enabled = !state.busy && host != null && (!repairCertificate || identitiesValid),
                ) { Text(stringResource(R.string.server_maintenance_repair)) }
            },
            dismissButton = {
                TextButton(onClick = cancelRepair, enabled = !state.busy) {
                    Text(stringResource(R.string.common_cancel))
                }
            },
        )
    }
}

@Composable
private fun MaintenanceHeader(host: ServerHostTarget?, busy: Boolean, onCheck: () -> Unit) {
    val endpoint = host?.let { "${it.sshHost}:${it.sshPort}" }.orEmpty()
    val address = host?.let { "${it.sshUserName}@$endpoint" }.orEmpty()
    val name = host?.displayName.orEmpty()
    val target = if (name.isBlank() || name == host?.sshUserName || name == endpoint ||
        name == address || name == host?.sshHost
    ) {
        address
    } else {
        "$name · $address"
    }
    Row(
        Modifier.fillMaxWidth().padding(start = Spacing.lg, end = Spacing.sm, top = Spacing.md, bottom = Spacing.sm),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
            Text(
                stringResource(R.string.server_maintenance_title),
                style = MaterialTheme.typography.headlineSmall,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                target,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
        }
        FilledTonalButton(
            onClick = onCheck,
            enabled = host != null && !busy,
            contentPadding = PaddingValues(horizontal = Spacing.md, vertical = Spacing.sm),
        ) {
            if (busy) {
                CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
            } else {
                DesktopIcon(DesktopIcons.refresh, size = 18.dp)
            }
            Text(
                stringResource(R.string.server_maintenance_check_short),
                style = MaterialTheme.typography.labelLarge,
                modifier = Modifier.padding(start = Spacing.xs),
            )
        }
    }
}

@Composable
private fun MaintenanceBanners(state: MaintenanceState, onRecover: (MaintenancePending) -> Unit) {
    state.pending?.let { pending ->
        MaintenanceBanner(
            tone = StatusTone.Danger,
            title = stringResource(R.string.server_maintenance_pending),
            detail = pending.reference.operationId,
            actionLabel = stringResource(R.string.server_maintenance_reconcile),
            actionEnabled = !state.busy,
            onAction = { onRecover(pending) },
        )
    }
    if (state.recovered != null) {
        MaintenanceBanner(
            tone = StatusTone.Info,
            title = stringResource(R.string.server_maintenance_reconciled),
        )
    }
    if (state.complete) {
        MaintenanceBanner(
            tone = StatusTone.Success,
            title = stringResource(R.string.server_maintenance_complete),
            detail = when (state.firewallStatus) {
                "disabled" -> stringResource(R.string.server_install_firewall_disabled)
                "ruleAdded" -> stringResource(R.string.server_install_firewall_added)
                else -> null
            },
        )
    }
}

/** One page-level answer: pending work, a reconciled receipt or a completed operation. */
@Composable
private fun MaintenanceBanner(
    tone: StatusTone,
    title: String,
    detail: String? = null,
    actionLabel: String? = null,
    actionEnabled: Boolean = true,
    onAction: (() -> Unit)? = null,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Radius.md),
        color = toneContainer(tone),
        contentColor = toneContent(tone),
    ) {
        Row(
            Modifier.padding(Spacing.md),
            verticalAlignment = Alignment.Top,
            horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
        ) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Text(title, style = MaterialTheme.typography.titleSmall)
                if (detail != null) Text(detail, style = MaterialTheme.typography.bodySmall)
            }
            if (actionLabel != null && onAction != null) {
                TextButton(
                    onClick = onAction,
                    enabled = actionEnabled,
                    colors = ButtonDefaults.textButtonColors(contentColor = toneContent(tone)),
                ) { Text(actionLabel) }
            }
        }
    }
}

@Composable
private fun MaintenanceOverview(
    snapshot: ServerHostSnapshot,
    busy: Boolean,
    pending: Boolean,
    installEnabled: Boolean,
    onActions: () -> Unit,
    onEnvironment: () -> Unit,
    onInstall: () -> Unit,
) {
    val enabled = !busy && !pending
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        SectionCard(
            title = stringResource(R.string.server_maintenance_status_title),
            subtitle = stringResource(R.string.server_maintenance_not_installed_note).takeUnless { snapshot.installed },
            leading = DesktopIcons.host,
        ) {
            StatusChip(
                text = stringResource(
                    when {
                        !snapshot.installed -> R.string.server_maintenance_absent
                        snapshot.healthy -> R.string.server_maintenance_healthy
                        else -> R.string.server_maintenance_unhealthy
                    },
                ),
                tone = when {
                    !snapshot.installed -> StatusTone.Neutral
                    snapshot.healthy -> StatusTone.Success
                    else -> StatusTone.Danger
                },
            )
            if (snapshot.installed) {
                MaintenanceDetail(R.string.server_maintenance_version, snapshot.version)
                MaintenanceDetail(R.string.server_maintenance_mode, modeText(snapshot.mode))
                MaintenanceDetail(R.string.server_maintenance_endpoint, snapshot.listenUrl)
                MaintenanceDetail(R.string.server_maintenance_checked_at, maintenanceTimestamp(snapshot.verifiedAtUtc))
                HorizontalDivider(Modifier.padding(vertical = Spacing.xs))
                Row(
                    Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                ) {
                    Button(onClick = onActions, enabled = enabled, modifier = Modifier.weight(1f)) {
                        Text(stringResource(R.string.server_maintenance_actions), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    OutlinedButton(onClick = onEnvironment, enabled = !busy, modifier = Modifier.weight(1f)) {
                        Text(stringResource(R.string.server_maintenance_environment), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
            } else {
                Row(
                    Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                ) {
                    Button(onClick = onInstall, enabled = enabled && installEnabled, modifier = Modifier.weight(1f)) {
                        Text(stringResource(R.string.ssh_workspace_deploy_install), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    OutlinedButton(onClick = onEnvironment, enabled = !busy, modifier = Modifier.weight(1f)) {
                        Text(stringResource(R.string.server_maintenance_environment), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
            }
        }
        if (snapshot.installed) {
            SectionCard(
                title = stringResource(R.string.server_maintenance_installation_details),
                leading = DesktopIcons.storage,
            ) {
                MaintenanceDetail(R.string.server_maintenance_install_root, snapshot.installRoot)
                MaintenanceDetail(R.string.server_maintenance_data_root, snapshot.dataRoot)
                MaintenanceDetail(R.string.server_maintenance_services, snapshot.serviceNames.joinToString().ifBlank { "—" })
                MaintenanceDetail(R.string.server_maintenance_identity, snapshot.installationId)
            }
        }
    }
}

@Composable
private fun MaintenanceEnvironment(
    snapshot: ServerHostSnapshot,
    probe: ServerHostProbe?,
    busy: Boolean,
    onCheck: () -> Unit,
) {
    if (probe == null) {
        EmptyState(
            text = stringResource(R.string.server_maintenance_environment_empty),
            icon = DesktopIcons.diagnostics,
            action = {
                OutlinedButton(onClick = onCheck, enabled = !busy) {
                    Text(stringResource(R.string.server_maintenance_check))
                }
            },
        )
        return
    }
    val osText = listOfNotNull(
        probe.osId?.takeIf { it.isNotBlank() }?.replaceFirstChar { it.uppercase() },
        probe.osVersion?.takeIf { it.isNotBlank() },
    ).joinToString(" ").ifBlank { "—" }
    val listenPort = snapshot.listenUrl?.let { runCatching { URI(it).port }.getOrNull() }
    val portUsedByInstallation = snapshot.installed && probe.requestedPort != null && listenPort == probe.requestedPort
    SectionCard(
        title = stringResource(R.string.server_maintenance_environment_title),
        subtitle = stringResource(R.string.server_maintenance_environment_subtitle),
        leading = DesktopIcons.system,
    ) {
        MaintenanceDetail(
            R.string.server_maintenance_environment_platform,
            listOfNotNull(probe.hostPlatform.replaceFirstChar { it.uppercase() }, probe.architecture)
                .filter(String::isNotBlank).joinToString(" · ").ifBlank { "—" },
        )
        MaintenanceDetail(R.string.server_maintenance_environment_os, osText)
        MaintenanceDetail(R.string.server_maintenance_environment_runtime, probe.runtimeIdentifier?.wireName() ?: "—")
        MaintenanceDetail(R.string.server_maintenance_environment_checked_at, maintenanceTimestamp(probe.verifiedAtUtc))
    }
    SectionCard(
        title = stringResource(R.string.server_maintenance_environment_checks),
        leading = DesktopIcons.diagnostics,
        contentSpacing = Spacing.sm,
    ) {
        EnvironmentCheckRow(
            title = stringResource(R.string.server_maintenance_environment_os_supported),
            value = stringResource(
                if (probe.osSupported) R.string.server_maintenance_environment_supported
                else R.string.server_maintenance_environment_unsupported,
            ),
            tone = if (probe.osSupported) StatusTone.Success else StatusTone.Danger,
        )
        EnvironmentCheckRow(
            title = stringResource(R.string.server_maintenance_environment_elevation),
            value = stringResource(
                if (probe.privilegeElevationAvailable) R.string.server_maintenance_environment_available
                else R.string.server_maintenance_environment_unavailable,
            ),
            tone = if (probe.privilegeElevationAvailable) StatusTone.Success else StatusTone.Warning,
        )
        if (probe.hostPlatform.equals("linux", true)) {
            EnvironmentCheckRow(
                title = stringResource(R.string.server_maintenance_environment_systemd),
                value = stringResource(
                    if (probe.systemdAvailable) R.string.server_maintenance_environment_available
                    else R.string.server_maintenance_environment_unavailable,
                ),
                tone = if (probe.systemdAvailable) StatusTone.Success else StatusTone.Warning,
            )
        }
        EnvironmentCheckRow(
            title = stringResource(R.string.server_maintenance_environment_disk),
            value = formatSize(probe.diskAvailableBytes) ?: stringResource(R.string.server_maintenance_environment_disk_none),
            tone = StatusTone.Info,
        )
        EnvironmentCheckRow(
            title = probe.requestedPort?.let { stringResource(R.string.server_maintenance_environment_port, it) }
                ?: stringResource(R.string.server_maintenance_environment_port_generic),
            value = when {
                portUsedByInstallation -> stringResource(R.string.server_maintenance_environment_port_installation)
                probe.requestedPortAvailable == true -> stringResource(R.string.server_maintenance_environment_available)
                probe.requestedPortAvailable == false -> stringResource(R.string.server_maintenance_environment_port_used)
                else -> stringResource(R.string.server_maintenance_environment_unknown)
            },
            tone = when {
                portUsedByInstallation -> StatusTone.Info
                probe.requestedPortAvailable == true -> StatusTone.Success
                probe.requestedPortAvailable == false -> StatusTone.Danger
                else -> StatusTone.Neutral
            },
        )
        EnvironmentCheckRow(
            title = stringResource(R.string.server_maintenance_environment_dependencies),
            value = if (probe.missingDependencies.isEmpty()) {
                stringResource(R.string.server_maintenance_environment_dependencies_complete)
            } else {
                stringResource(R.string.server_maintenance_environment_dependencies_missing, probe.missingDependencies.size)
            },
            supporting = probe.missingDependencies.takeIf { it.isNotEmpty() }?.joinToString(),
            tone = if (probe.missingDependencies.isEmpty()) StatusTone.Success else StatusTone.Danger,
        )
        Text(
            stringResource(R.string.server_maintenance_environment_note),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun MaintenanceActions(
    snapshot: ServerHostSnapshot,
    busy: Boolean,
    pending: Boolean,
    installEnabled: Boolean,
    sudo: String,
    onSudoChange: (String) -> Unit,
    onInstall: () -> Unit,
    onUpgrade: () -> Unit,
    onComponents: () -> Unit,
    onSettings: () -> Unit,
    onRepair: () -> Unit,
    onAddFirewall: () -> Unit,
    onRemoveFirewall: () -> Unit,
    onUninstall: () -> Unit,
) {
    if (!snapshot.installed) {
        SectionCard(
            title = stringResource(R.string.server_maintenance_status_title),
            subtitle = stringResource(R.string.server_maintenance_not_installed_note),
            leading = DesktopIcons.host,
        ) {
            Button(onClick = onInstall, enabled = !busy && !pending && installEnabled, modifier = Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.ssh_workspace_deploy_install))
            }
        }
        return
    }
    val enabled = !busy && !pending
    Text(
        stringResource(R.string.server_maintenance_actions_note),
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    if (snapshot.mode == ServerInstallMode.LinuxSystem) {
        SectionCard(
            title = stringResource(R.string.server_maintenance_actions_credentials),
            leading = DesktopIcons.credentials,
        ) {
            PasswordTextField(
                value = sudo,
                onValueChange = onSudoChange,
                label = stringResource(R.string.server_maintenance_password_label),
                enabled = !busy,
                supportingText = { Text(stringResource(R.string.server_maintenance_password_note)) },
            )
        }
    }
    SectionCard(
        title = stringResource(R.string.server_maintenance_actions_services),
        leading = DesktopIcons.deployments,
        contentSpacing = Spacing.xs,
    ) {
        MaintenanceActionRow(
            icon = DesktopIcons.deployments,
            title = stringResource(R.string.server_maintenance_upgrade_title),
            subtitle = stringResource(R.string.server_maintenance_upgrade_note),
            enabled = enabled,
            onClick = onUpgrade,
        )
        HorizontalDivider()
        MaintenanceActionRow(
            icon = DesktopIcons.applications,
            title = stringResource(R.string.server_configuration_components),
            subtitle = stringResource(R.string.server_maintenance_components_note),
            enabled = enabled && snapshot.guardianEnabled != null,
            onClick = onComponents,
        )
        HorizontalDivider()
        MaintenanceActionRow(
            icon = DesktopIcons.hostSettings,
            title = stringResource(R.string.server_configuration_settings),
            subtitle = stringResource(R.string.server_maintenance_settings_note),
            enabled = enabled && snapshot.guardianEnabled != null,
            onClick = onSettings,
        )
        HorizontalDivider()
        MaintenanceActionRow(
            icon = DesktopIcons.operations,
            title = stringResource(R.string.server_maintenance_repair),
            subtitle = stringResource(R.string.server_maintenance_repair_note),
            enabled = enabled,
            onClick = onRepair,
        )
    }
    if (snapshot.mode != ServerInstallMode.LinuxUser) {
        SectionCard(
            title = stringResource(R.string.server_maintenance_actions_network),
            leading = DesktopIcons.firewall,
            contentSpacing = Spacing.xs,
        ) {
            MaintenanceActionRow(
                icon = DesktopIcons.firewall,
                title = stringResource(R.string.server_maintenance_firewall_add_title),
                subtitle = stringResource(R.string.server_maintenance_firewall_add_note),
                enabled = enabled,
                onClick = onAddFirewall,
            )
            HorizontalDivider()
            MaintenanceActionRow(
                icon = DesktopIcons.firewall,
                title = stringResource(R.string.server_maintenance_firewall_remove_title),
                subtitle = stringResource(R.string.server_maintenance_firewall_remove_note),
                enabled = enabled,
                onClick = onRemoveFirewall,
            )
        }
    }
    SectionCard(
        title = stringResource(R.string.server_maintenance_actions_danger),
        leading = DesktopIcons.delete,
        contentSpacing = Spacing.xs,
    ) {
        MaintenanceActionRow(
            icon = DesktopIcons.delete,
            title = stringResource(R.string.server_maintenance_uninstall),
            subtitle = stringResource(R.string.server_maintenance_uninstall_note_short),
            enabled = enabled,
            danger = true,
            onClick = onUninstall,
        )
    }
}

@Composable
private fun MaintenanceActionRow(
    @DrawableRes icon: Int,
    title: String,
    subtitle: String,
    enabled: Boolean,
    onClick: () -> Unit,
    danger: Boolean = false,
) {
    Surface(
        onClick = onClick,
        enabled = enabled,
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Radius.md),
        color = Color.Transparent,
    ) {
        Row(
            Modifier
                .alpha(if (enabled) 1f else 0.45f)
                .heightIn(min = 64.dp)
                .padding(horizontal = Spacing.sm, vertical = Spacing.sm),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(Spacing.md),
        ) {
            IconBadge(icon)
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(
                    title,
                    style = MaterialTheme.typography.bodyLarge,
                    color = if (danger) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = if (danger) MaterialTheme.colorScheme.error.copy(alpha = 0.8f)
                    else MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            DesktopIcon(DesktopIcons.disclosure, size = 18.dp)
        }
    }
}

@Composable
private fun EnvironmentCheckRow(
    title: String,
    value: String,
    tone: StatusTone,
    supporting: String? = null,
) {
    Row(
        Modifier.fillMaxWidth().heightIn(min = 48.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, style = MaterialTheme.typography.bodyMedium)
            if (supporting != null) {
                Text(
                    supporting,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        StatusChip(text = value, tone = tone)
    }
}

@Composable
private fun MaintenanceDetail(@StringRes label: Int, value: String?) {
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
        Text(stringResource(label), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        androidx.compose.foundation.text.selection.SelectionContainer {
            Text(value?.takeIf { it.isNotBlank() } ?: "—", style = MaterialTheme.typography.bodyMedium)
        }
    }
}

@Composable
private fun modeText(mode: ServerInstallMode?): String =
    stringResource(
        when (mode) {
            ServerInstallMode.LinuxSystem -> R.string.server_maintenance_mode_linux_system
            ServerInstallMode.LinuxUser -> R.string.server_maintenance_mode_linux_user
            ServerInstallMode.WindowsSystem -> R.string.server_maintenance_mode_windows_system
            null -> R.string.server_maintenance_mode_unknown
        },
    )

internal fun maintenanceTimestamp(value: String?): String {
    if (value.isNullOrBlank()) return "—"
    return IsoInstant.toEpochMillis(value)?.let(::formatTimestamp) ?: value
}
