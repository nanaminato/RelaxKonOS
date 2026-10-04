package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.ui.common.rememberUsageOpenDocument

import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import app.relaxkonos.mobile.ui.common.StatusTone
import android.net.Uri
import app.relaxkonos.mobile.data.resolvedDisplayName
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.core.layout.layoutStateFor
import app.relaxkonos.mobile.ui.more.AppearanceScreen
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.Tab
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.ServerHostTarget
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.SelectField
import app.relaxkonos.mobile.ui.common.SelectOption
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

/** Uses the authenticated shell's window classes and inset ownership. */
@Composable
fun SshWorkspaceScreen(hostId: String, onClose: () -> Unit) {
    var page by rememberSaveable(hostId) { mutableIntStateOf(0) }
    var managementPage by rememberSaveable(hostId) { mutableIntStateOf(0) }
    val host = (LocalContext.current.applicationContext as RelaxKonApplication)
        .container.serverCenter.hosts().firstOrNull { it.hostId == hostId }
    val terminalTyping = page == 1 && WindowInsets.ime.getBottom(LocalDensity.current) > 0
    androidx.activity.compose.BackHandler {
        if (page != 3) { page = 3; managementPage = 0 }
        else if (managementPage != 0) managementPage = 0
        else onClose()
    }
    val content: @Composable (Modifier) -> Unit = { contentModifier ->
        when (page) {
            0 -> SshFilesScreen(hostId, contentModifier)
            1 -> SshTerminalScreen(hostId, onClose, contentModifier)
            2 -> ServerMaintenanceScreen(host, contentModifier)
            3 -> Column(contentModifier) {
                SshManagementTabs(managementPage, { managementPage = it })
                val body = Modifier.weight(1f).fillMaxWidth()
                when (managementPage) {
                    0 -> SshSystemScreen(hostId, onClose, body)
                    1 -> AppearanceScreen(onBack = { managementPage = 0 }, modifier = body,
                        titleRes = R.string.ssh_workspace_settings)
                    2 -> SshForwardsScreen(hostId, body)
                }
            }
        }
    }
    SshWorkspaceLayout(page, { page = it }, terminalTyping, content = content)
}

@Composable
internal fun SshManagementTabs(selected: Int, onSelect: (Int) -> Unit) {
    val labels = listOf(R.string.ssh_workspace_system, R.string.ssh_workspace_settings, R.string.ssh_forward_title)
    PrimaryTabRow(selectedTabIndex = selected) {
        labels.forEachIndexed { index, label ->
            Tab(selected = selected == index, onClick = { onSelect(index) },
                text = { Text(stringResource(label)) })
        }
    }
}

@Composable
internal fun SshWorkspaceLayout(
    page: Int,
    onSelect: (Int) -> Unit,
    terminalTyping: Boolean,
    modifier: Modifier = Modifier,
    content: @Composable (Modifier) -> Unit,
) {
    val destinations = listOf(
        R.string.ssh_files_title to DesktopIcons.navFiles,
        R.string.ssh_terminal_title to DesktopIcons.navTerminal,
        R.string.ssh_workspace_deploy to DesktopIcons.deployments,
        R.string.ssh_workspace_management to DesktopIcons.navMore,
    )
    BoxWithConstraints(modifier.fillMaxSize()) {
        val layout = layoutStateFor(maxWidth)
        if (layout == LayoutState.Compact) Scaffold(
            containerColor = androidx.compose.ui.graphics.Color.Transparent,
            bottomBar = {
                if (!terminalTyping) NavigationBar(Modifier.testTag("ssh-workspace-bar")) {
                    destinations.forEachIndexed { index, (label, icon) ->
                        NavigationBarItem(selected = page == index, onClick = { onSelect(index) },
                            icon = { DesktopIcon(icon, size = 26.dp) },
                            label = { Text(stringResource(label), maxLines = 1) })
                    }
                }
            },
        ) { padding ->
            content(Modifier.fillMaxSize().padding(padding).consumeWindowInsets(padding))
        } else Row(Modifier.fillMaxSize().safeDrawingPadding()) {
            NavigationRail(
                modifier = Modifier.fillMaxHeight().testTag("ssh-workspace-rail"),
                containerColor = MaterialTheme.colorScheme.surface,
                windowInsets = WindowInsets(0),
                header = { DesktopIcon(DesktopIcons.brand, size = 36.dp,
                    modifier = Modifier.padding(bottom = Spacing.md)) },
            ) {
                destinations.forEachIndexed { index, (label, icon) ->
                    val destinationLabel = stringResource(label)
                    NavigationRailItem(selected = page == index, onClick = { onSelect(index) },
                        modifier = Modifier.semantics { contentDescription = destinationLabel },
                        icon = { DesktopIcon(icon, size = 26.dp) },
                        label = { if (layout == LayoutState.Expanded) Text(stringResource(label)) })
                }
            }
            content(Modifier.weight(1f).fillMaxHeight().padding(horizontal = Spacing.xs))
        }
    }
}

/** Matches the desktop source → mode → review flow. Executes the embedded launcher with source-specific package checks. */
@Composable
internal fun DeploymentSetupScreen(host: ServerHostTarget?, modifier: Modifier = Modifier, onBusyChanged: (Boolean) -> Unit = {}) {
    val resolver = LocalContext.current.contentResolver
    val hostId = host?.hostId
    val installKey = remember(hostId) { "install-${host?.hostId}-${host?.lastVerified?.verifiedAtEpochMillis}" }
    val installer: ServerInstallViewModel = viewModel(key = installKey)
    val installState by installer.state.collectAsStateWithLifecycle()
    androidx.compose.runtime.LaunchedEffect(installState.busy) { onBusyChanged(installState.busy) }
    var bundleUri by rememberSaveable(hostId) { mutableStateOf<String?>(null) }
    var certificateUri by rememberSaveable(hostId) { mutableStateOf<String?>(null) }
    var privateKeyUri by rememberSaveable(hostId) { mutableStateOf<String?>(null) }
    var step by rememberSaveable(hostId) { mutableIntStateOf(0) }
    var source by rememberSaveable(hostId) { mutableStateOf("official") }
    var bundleName by rememberSaveable(hostId) { mutableStateOf("") }
    var remoteBundlePath by rememberSaveable(hostId) { mutableStateOf("") }
    var browseRemoteBundle by remember(hostId) { mutableStateOf(false) }
    var mode by rememberSaveable(hostId) { mutableStateOf(initialInstallMode(host)) }
    var advanced by rememberSaveable(hostId) { mutableStateOf(ServerInstallAdvancedOptions(
        serverPort = initialInstallPort(host))) }
    var fileAccess by rememberSaveable(hostId) { mutableStateOf("restricted") }
    var network by rememberSaveable(hostId) { mutableStateOf("loopback") }
    var certificateMode by rememberSaveable(hostId) { mutableStateOf(initialInstallCertificate(host)) }
    var certificateFormat by rememberSaveable(hostId) { mutableStateOf("pfx") }
    var certificateName by rememberSaveable(hostId) { mutableStateOf("") }
    var privateKeyName by rememberSaveable(hostId) { mutableStateOf("") }
    var certificatePassword by remember(hostId) { mutableStateOf("") }
    var sudoPassword by remember(hostId) { mutableStateOf("") }
    var certificateNames by rememberSaveable(hostId) { mutableStateOf("localhost,127.0.0.1") }
    val pickBundle = rememberLauncherForActivityResult(rememberUsageOpenDocument("SshWorkspaceScreen.bundle.$hostId")) { uri ->
        bundleUri = uri?.toString()
        bundleName = uri?.let { resolver.resolvedDisplayName(it) }.orEmpty()
    }
    val pickCertificate = rememberLauncherForActivityResult(rememberUsageOpenDocument("SshWorkspaceScreen.certificate.$hostId")) { uri ->
        certificateUri = uri?.toString()
        certificateName = uri?.let { resolver.resolvedDisplayName(it) }.orEmpty()
    }
    val pickPrivateKey = rememberLauncherForActivityResult(rememberUsageOpenDocument("SshWorkspaceScreen.private-key.$hostId")) { uri ->
        privateKeyUri = uri?.toString()
        privateKeyName = uri?.let { resolver.resolvedDisplayName(it) }.orEmpty()
    }
    val mayContinue = when (step) {
        0 -> source == "official" || (source == "local" && bundleName.isNotBlank()) ||
            (source == "remote" && remoteBundlePath.trim().endsWith(".zip", ignoreCase = true)) ||
            (source == "url" && advanced.packageUri.startsWith("https://") && advanced.packageDigest.matches(Regex("[0-9a-fA-F]{64}")))
        1 -> advanced.serverPort.toIntOrNull()?.let { it in 1..65535 } == true &&
            (fileAccess != "whitelist" || advanced.fileRoots.isNotBlank()) &&
            (mode != "linuxSystem" || (advanced.administratorFileAccess != "whitelist" || advanced.administratorFileRoots.isNotBlank()) && (advanced.rootFileAccess != "whitelist" || advanced.rootFileRoots.isNotBlank())) &&
            (certificateMode != "custom" || (certificateName.isNotBlank() && (certificateFormat != "pem" || privateKeyName.isNotBlank())))
        else -> false
    }

    // Resize the scroll viewport above the keyboard so TextField focus relocation
    // can keep the edited field visible, including the final certificate names field.
    Column(modifier.fillMaxSize().imePadding()) {
        if (installState.busy) Column(
            Modifier.fillMaxWidth().padding(horizontal = Spacing.lg, vertical = Spacing.sm),
            verticalArrangement = Arrangement.spacedBy(Spacing.sm),
        ) {
            Text(stringResource(installState.message ?: R.string.ssh_workspace_deploy_running),
                style = MaterialTheme.typography.titleSmall)
            val transfer = installState.transfer
            if (transfer != null) app.relaxkonos.mobile.ui.common.TransferProgressContent(transfer)
            else androidx.compose.material3.LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
        }
        Column(
            Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Spacing.md),
        ) {
            Text(stringResource(R.string.ssh_workspace_deploy_step, step + 1), color = MaterialTheme.colorScheme.onSurfaceVariant)
            when (step) {
                0 -> SectionCard(
                    title = stringResource(R.string.ssh_workspace_deploy_step_source),
                    subtitle = stringResource(R.string.ssh_workspace_deploy_draft),
                    leading = DesktopIcons.deployments,
                ) {
                    SelectField(
                        label = stringResource(R.string.ssh_workspace_deploy_step_source),
                        options = listOf(
                            SelectOption("official", stringResource(R.string.ssh_workspace_deploy_source_official)),
                            SelectOption("local", stringResource(R.string.ssh_workspace_deploy_source_local)),
                            SelectOption("remote", stringResource(R.string.ssh_workspace_deploy_source_remote)),
                            SelectOption("url", stringResource(R.string.server_install_source_url)),
                        ),
                        value = source,
                        onValueChange = { source = it },
                    )
                    if (source == "url") {
                    OutlinedTextField(value = advanced.packageUri, onValueChange = { advanced = advanced.copy(packageUri = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_package_uri)) })
                    OutlinedTextField(value = advanced.packageDigest, onValueChange = { advanced = advanced.copy(packageDigest = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_package_digest)) })
                    }
                    if (source == "official") OutlinedTextField(
                        value = advanced.releaseCatalogBaseUri,
                        onValueChange = { advanced = advanced.copy(releaseCatalogBaseUri = it) },
                        modifier = Modifier.fillMaxWidth(),
                        label = { Text(stringResource(R.string.server_install_release_catalog)) },
                        placeholder = { Text("https://downloads.relaxkon.com/relaxkonos/stable/latest") },
                        singleLine = true,
                    )
                    if (source == "local") {
                        OutlinedButton({ pickBundle.launch(arrayOf("application/zip", "application/octet-stream")) }) {
                            Text(stringResource(R.string.ssh_workspace_deploy_choose_bundle))
                        }
                        if (bundleName.isNotBlank()) Text(bundleName, style = MaterialTheme.typography.bodySmall)
                    }
                    if (source == "remote") {
                        OutlinedTextField(
                            value = remoteBundlePath,
                            onValueChange = { remoteBundlePath = it },
                            modifier = Modifier.fillMaxWidth(),
                            label = { Text(stringResource(R.string.ssh_workspace_deploy_remote_bundle_path)) },
                            singleLine = true,
                        )
                        OutlinedButton(onClick = { browseRemoteBundle = true }, enabled = host != null) {
                            Text(stringResource(R.string.remote_path_browse))
                        }
                    }
                    if (!mayContinue) Text(stringResource(R.string.ssh_workspace_deploy_bundle_required), color = MaterialTheme.colorScheme.error)
                }

                1 -> SectionCard(
                    title = stringResource(R.string.ssh_workspace_deploy_step_mode),
                    subtitle = stringResource(R.string.ssh_workspace_deploy_draft),
                    leading = DesktopIcons.deployments,
                ) {
                    SelectField(
                        label = stringResource(R.string.ssh_workspace_deploy_mode),
                        options = listOf(
                            SelectOption("automatic", stringResource(R.string.ssh_workspace_deploy_automatic)),
                            SelectOption("linuxUser", stringResource(R.string.ssh_workspace_deploy_linux_user)),
                            SelectOption("linuxSystem", stringResource(R.string.ssh_workspace_deploy_linux_system)),
                            SelectOption("windowsSystem", stringResource(R.string.ssh_workspace_deploy_windows_system)),
                        ),
                        value = mode,
                        onValueChange = { mode = it; if (it == "linuxUser") { network = "loopback"; certificateMode = "none"; fileAccess = "restricted" } },
                    )
                    if (mode == "linuxSystem") PasswordTextField(sudoPassword, { sudoPassword = it },
                        stringResource(R.string.ssh_workspace_deploy_sudo_password))
                    if (mode != "linuxUser") {
                    SelectField(
                        label = stringResource(R.string.ssh_workspace_deploy_file_access),
                        options = listOf(
                            SelectOption("restricted", stringResource(R.string.ssh_workspace_deploy_restricted)),
                            SelectOption("whitelist", stringResource(R.string.server_install_file_access_whitelist)),
                            SelectOption("full", stringResource(R.string.ssh_workspace_deploy_full_access)),
                        ),
                        value = fileAccess,
                        onValueChange = { fileAccess = it },
                    )
                    SelectField(
                        label = stringResource(R.string.ssh_workspace_deploy_network),
                        options = listOf(
                            SelectOption("loopback", stringResource(R.string.ssh_workspace_deploy_loopback)),
                            SelectOption("lan", stringResource(R.string.ssh_workspace_deploy_lan)),
                        ),
                        value = network,
                        onValueChange = { network = it },
                        supportingText = stringResource(R.string.ssh_workspace_deploy_lan_note).takeIf { network == "lan" },
                    )
                    if (mode != "linuxUser" && network == "lan") {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(advanced.addFirewallRule, { advanced = advanced.copy(addFirewallRule = it) })
                            Text(stringResource(R.string.server_install_firewall_choice))
                        }
                        Text(stringResource(R.string.server_install_firewall_help), style = MaterialTheme.typography.bodySmall)
                    }
                    SelectField(
                        label = stringResource(R.string.ssh_workspace_deploy_certificate),
                        options = listOf(
                            SelectOption("none", stringResource(R.string.ssh_workspace_deploy_certificate_none)),
                            SelectOption("custom", stringResource(R.string.ssh_workspace_deploy_certificate_custom)),
                            SelectOption("selfSigned", stringResource(R.string.ssh_workspace_deploy_certificate_self_signed)),
                        ),
                        value = certificateMode,
                        onValueChange = { certificateMode = it },
                    )
                    if (certificateMode == "custom") {
                        SelectField(
                            label = stringResource(R.string.ssh_workspace_deploy_certificate_format),
                            options = listOf(
                                SelectOption("pfx", stringResource(R.string.ssh_workspace_deploy_certificate_pfx)),
                                SelectOption("pem", stringResource(R.string.ssh_workspace_deploy_certificate_pem)),
                            ),
                            value = certificateFormat,
                            onValueChange = { certificateFormat = it },
                        )
                        OutlinedButton({ pickCertificate.launch(arrayOf("*/*")) }) { Text(stringResource(R.string.ssh_workspace_deploy_choose_certificate)) }
                        if (certificateName.isNotBlank()) Text(certificateName, style = MaterialTheme.typography.bodySmall)
                        if (certificateFormat == "pem") {
                            OutlinedButton({ pickPrivateKey.launch(arrayOf("*/*")) }) { Text(stringResource(R.string.ssh_workspace_deploy_choose_private_key)) }
                            if (privateKeyName.isNotBlank()) Text(privateKeyName, style = MaterialTheme.typography.bodySmall)
                        }
                        PasswordTextField(certificatePassword, { certificatePassword = it }, stringResource(R.string.ssh_workspace_deploy_certificate_password))
                    }
                    if (certificateMode == "selfSigned") {
                        OutlinedTextField(
                            value = certificateNames,
                            onValueChange = { certificateNames = it },
                            modifier = Modifier.fillMaxWidth(),
                            label = { Text(stringResource(R.string.ssh_workspace_deploy_certificate_names)) },
                        )
                    }
                    }
                    Text(stringResource(R.string.server_install_advanced), style = MaterialTheme.typography.titleSmall)
                    OutlinedTextField(value = advanced.serverPort, onValueChange = { advanced = advanced.copy(serverPort = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_server_port)) })
                    OutlinedTextField(value = advanced.dataRoot, onValueChange = { advanced = advanced.copy(dataRoot = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_data_root)) })
                    if (mode != "linuxUser") {
                    OutlinedTextField(value = advanced.installRoot, onValueChange = { advanced = advanced.copy(installRoot = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_install_root)) })
                    if (fileAccess == "whitelist") {
                    OutlinedTextField(value = advanced.fileRoots, onValueChange = { advanced = advanced.copy(fileRoots = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_file_roots)) })
                    }
                    }
                    if (mode == "linuxUser") {
                    OutlinedTextField(value = advanced.configRoot, onValueChange = { advanced = advanced.copy(configRoot = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_config_root)) })
                    OutlinedTextField(value = advanced.stateRoot, onValueChange = { advanced = advanced.copy(stateRoot = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_state_root)) })
                    OutlinedTextField(value = advanced.cacheRoot, onValueChange = { advanced = advanced.copy(cacheRoot = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_cache_root)) })
                    }
                    if (mode == "linuxSystem") {
                    SelectField(label = stringResource(R.string.server_install_administrator_access), options = listOf(
                        SelectOption("restricted", stringResource(R.string.ssh_workspace_deploy_restricted)),
                        SelectOption("whitelist", stringResource(R.string.server_install_file_access_whitelist)),
                        SelectOption("full", stringResource(R.string.ssh_workspace_deploy_full_access))), value = advanced.administratorFileAccess, onValueChange = { advanced = advanced.copy(administratorFileAccess = it) })
                    if (advanced.administratorFileAccess == "whitelist") {
                    OutlinedTextField(value = advanced.administratorFileRoots, onValueChange = { advanced = advanced.copy(administratorFileRoots = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_file_roots)) })
                    }
                    SelectField(label = stringResource(R.string.server_install_root_access), options = listOf(
                        SelectOption("restricted", stringResource(R.string.ssh_workspace_deploy_restricted)),
                        SelectOption("whitelist", stringResource(R.string.server_install_file_access_whitelist)),
                        SelectOption("full", stringResource(R.string.ssh_workspace_deploy_full_access))), value = advanced.rootFileAccess, onValueChange = { advanced = advanced.copy(rootFileAccess = it) })
                    if (advanced.rootFileAccess == "whitelist") {
                    OutlinedTextField(value = advanced.rootFileRoots, onValueChange = { advanced = advanced.copy(rootFileRoots = it) }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.server_install_file_roots)) })
                    }
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(checked = advanced.dockerAccess, onCheckedChange = { advanced = advanced.copy(dockerAccess = it) })
                        Text(stringResource(R.string.server_install_docker_access), modifier = Modifier.weight(1f))
                    }
                    }
                    if (mode != "windowsSystem") Row(verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(checked = advanced.allowUnsupportedSystem, onCheckedChange = { advanced = advanced.copy(allowUnsupportedSystem = it) })
                        Text(stringResource(R.string.server_install_allow_unsupported), modifier = Modifier.weight(1f))
                    }
                    if (!mayContinue) Text(stringResource(R.string.server_install_options_invalid), color = MaterialTheme.colorScheme.error)
                }

                else -> SectionCard(
                    title = stringResource(R.string.ssh_workspace_deploy_step_review),
                    subtitle = stringResource(R.string.ssh_workspace_deploy_draft),
                    leading = DesktopIcons.deployments,
                ) {
                    ReviewLine(stringResource(R.string.ssh_workspace_deploy_target), host?.let { "${it.displayName} · ${it.sshUserName}" }.orEmpty())
                    ReviewLine(stringResource(R.string.ssh_workspace_deploy_source), when (source) {
                        "local" -> stringResource(R.string.ssh_workspace_deploy_source_local) + " · " + bundleName
                        "remote" -> stringResource(R.string.ssh_workspace_deploy_source_remote) + " · " + remoteBundlePath
                        "url" -> stringResource(R.string.server_install_source_url) + " · " + advanced.packageUri
                        else -> stringResource(R.string.ssh_workspace_deploy_source_official)
                    })
                    ReviewLine(stringResource(R.string.ssh_workspace_deploy_mode), when (mode) {
                        "linuxUser" -> stringResource(R.string.ssh_workspace_deploy_linux_user)
                        "linuxSystem" -> stringResource(R.string.ssh_workspace_deploy_linux_system)
                        "windowsSystem" -> stringResource(R.string.ssh_workspace_deploy_windows_system)
                        else -> stringResource(R.string.ssh_workspace_deploy_automatic)
                    })
                    ReviewLine(stringResource(R.string.ssh_workspace_deploy_file_access), stringResource(when (fileAccess) { "full" -> R.string.ssh_workspace_deploy_full_access; "whitelist" -> R.string.server_install_file_access_whitelist; else -> R.string.ssh_workspace_deploy_restricted }))
                    ReviewLine(stringResource(R.string.ssh_workspace_deploy_network), stringResource(if (network == "lan") R.string.ssh_workspace_deploy_lan else R.string.ssh_workspace_deploy_loopback))
                    ReviewLine(stringResource(R.string.ssh_workspace_deploy_certificate), when (certificateMode) {
                        "custom" -> stringResource(R.string.ssh_workspace_deploy_certificate_custom)
                        "selfSigned" -> stringResource(R.string.ssh_workspace_deploy_certificate_self_signed)
                        else -> stringResource(R.string.ssh_workspace_deploy_certificate_none)
                    })
                    ReviewLine(stringResource(R.string.server_install_server_port), advanced.serverPort)
                    ReviewLine(stringResource(R.string.server_install_install_root), advanced.installRoot)
                    ReviewLine(stringResource(R.string.server_install_data_root), advanced.dataRoot)
                    ReviewLine(stringResource(R.string.server_install_config_root), advanced.configRoot)
                    ReviewLine(stringResource(R.string.server_install_state_root), advanced.stateRoot)
                    ReviewLine(stringResource(R.string.server_install_cache_root), advanced.cacheRoot)
                    ReviewLine(stringResource(R.string.server_install_file_roots), advanced.fileRoots)
                    ReviewLine(stringResource(R.string.server_install_administrator_access), advanced.administratorFileAccess)
                    ReviewLine(stringResource(R.string.server_install_file_roots), advanced.administratorFileRoots)
                    ReviewLine(stringResource(R.string.server_install_root_access), advanced.rootFileAccess)
                    ReviewLine(stringResource(R.string.server_install_file_roots), advanced.rootFileRoots)
                    if (source == "official") ReviewLine(stringResource(R.string.server_install_release_catalog), advanced.releaseCatalogBaseUri)
                    ReviewLine(stringResource(R.string.server_install_package_digest), advanced.packageDigest)
                    if (mode == "linuxSystem") ReviewLine(stringResource(R.string.server_install_docker_access), advanced.dockerAccess.toString())
                    if (mode != "windowsSystem") ReviewLine(stringResource(R.string.server_install_allow_unsupported), advanced.allowUnsupportedSystem.toString())
                    if (mode != "linuxUser" && network == "lan") ReviewLine(stringResource(R.string.server_install_firewall_choice), advanced.addFirewallRule.toString())
                    Text(stringResource(R.string.ssh_workspace_deploy_source_checks), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    installState.message?.takeUnless { installState.busy }?.let {
                        if (installState.installed && it != R.string.server_install_firewall_disabled) Text(stringResource(it), color = MaterialTheme.colorScheme.primary)
                        else OperationMessageDialog(stringResource(it), tone = if (it == R.string.ssh_workspace_deploy_verify || it == R.string.server_install_firewall_disabled) StatusTone.Warning else StatusTone.Danger)
                    }
                }
            }
        }
        Row(
            Modifier.fillMaxWidth().padding(Spacing.lg),
            horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
        ) {
            if (step > 0) OutlinedButton(onClick = { step-- }, enabled = !installState.busy, modifier = Modifier.weight(1f)) {
                Text(stringResource(R.string.common_back))
            }
            if (step < 2) Button(onClick = { step++ }, enabled = mayContinue, modifier = Modifier.weight(1f)) {
                Text(stringResource(R.string.ssh_workspace_deploy_next))
            } else Button(onClick = {
                host?.let { installer.install(ServerInstallSelection(it.hostId, source,
                    bundleUri?.let(Uri::parse), remoteBundlePath, mode, network, fileAccess,
                    certificateMode, certificateFormat, certificateUri?.let(Uri::parse),
                    privateKeyUri?.let(Uri::parse), certificatePassword, certificateNames, sudoPassword,
                    advanced.copy(releaseCatalogBaseUri = if (source == "official") advanced.releaseCatalogBaseUri else ""))) }
                sudoPassword = ""
            }, enabled = host != null && !installState.busy && !installState.installed, modifier = Modifier.weight(1f)) {
                Text(stringResource(when {
                    installState.installed -> R.string.ssh_workspace_deploy_installed
                    host?.lastVerified?.installed == true -> R.string.installation_kind_upgrade
                    else -> R.string.ssh_workspace_deploy_install
                }))
            }
        }
    }
    if (browseRemoteBundle && host != null) SshBundlePicker(
        hostId = host.hostId,
        onDismiss = { browseRemoteBundle = false },
        onSelect = { remoteBundlePath = it; browseRemoteBundle = false },
    )
}

@Composable
private fun ReviewLine(label: String, value: String) {
    Column(Modifier.fillMaxWidth()) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}

private fun initialInstallPort(host: ServerHostTarget?): String {
    val verified = host?.lastVerified ?: return "5000"
    val url = verified.listenUrl ?: return "5000"
    val port = Uri.parse(url).port
    return if (port in 1..65535) port.toString() else "5000"
}
private fun initialInstallMode(host: ServerHostTarget?): String {
    val verified = host?.lastVerified ?: return "automatic"
    val mode = verified.mode ?: return "automatic"
    return mode.name.replaceFirstChar { it.lowercase() }
}
private fun initialInstallCertificate(host: ServerHostTarget?): String {
    val verified = host?.lastVerified ?: return "none"
    val url = verified.listenUrl ?: return "none"
    return if (url.startsWith("https://")) "selfSigned" else "none"
}
