package app.relaxkonos.mobile.ui.servercenter

import android.net.Uri
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.runtime.collectAsState
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
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
    val host = (LocalContext.current.applicationContext as RelaxKonApplication)
        .container.serverCenter.hosts().firstOrNull { it.hostId == hostId }
    val terminalTyping = page == 1 && WindowInsets.ime.getBottom(LocalDensity.current) > 0
    androidx.activity.compose.BackHandler { if (page != 3) page = 3 else onClose() }
    val content: @Composable (Modifier) -> Unit = { contentModifier ->
        when (page) {
            0 -> SshFilesScreen(hostId, contentModifier)
            1 -> SshTerminalScreen(hostId, onClose, contentModifier)
            2 -> ServerMaintenanceScreen(host, contentModifier)
            3 -> SshSystemScreen(hostId, onClose, contentModifier)
            4 -> AppearanceScreen(onBack = { page = 3 }, modifier = contentModifier,
                titleRes = R.string.ssh_workspace_settings)
            else -> SshForwardsScreen(hostId, contentModifier)
        }
    }
    SshWorkspaceLayout(page, { page = it }, terminalTyping, content = content)
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
        R.string.ssh_workspace_system to DesktopIcons.system,
        R.string.ssh_workspace_settings to DesktopIcons.navMore,
        R.string.ssh_forward_title to DesktopIcons.connections,
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
    val installKey = remember(host?.hostId) { "install-${host?.hostId}-${host?.lastVerified?.verifiedAtEpochMillis}" }
    val installer: ServerInstallViewModel = viewModel(key = installKey)
    val installState by installer.state.collectAsState()
    androidx.compose.runtime.LaunchedEffect(installState.busy) { onBusyChanged(installState.busy) }
    var bundleUri by rememberSaveable(host?.hostId) { mutableStateOf<String?>(null) }
    var certificateUri by rememberSaveable(host?.hostId) { mutableStateOf<String?>(null) }
    var privateKeyUri by rememberSaveable(host?.hostId) { mutableStateOf<String?>(null) }
    var step by rememberSaveable(host?.hostId) { mutableIntStateOf(0) }
    var source by rememberSaveable(host?.hostId) { mutableStateOf("official") }
    var bundleName by rememberSaveable(host?.hostId) { mutableStateOf("") }
    var remoteBundlePath by rememberSaveable(host?.hostId) { mutableStateOf("") }
    var browseRemoteBundle by remember(host?.hostId) { mutableStateOf(false) }
    var mode by rememberSaveable(host?.hostId) { mutableStateOf("automatic") }
    var fileAccess by rememberSaveable(host?.hostId) { mutableStateOf("restricted") }
    var network by rememberSaveable(host?.hostId) { mutableStateOf("loopback") }
    var certificateMode by rememberSaveable(host?.hostId) { mutableStateOf("none") }
    var certificateFormat by rememberSaveable(host?.hostId) { mutableStateOf("pfx") }
    var certificateName by rememberSaveable(host?.hostId) { mutableStateOf("") }
    var privateKeyName by rememberSaveable(host?.hostId) { mutableStateOf("") }
    var certificatePassword by remember(host?.hostId) { mutableStateOf("") }
    var sudoPassword by remember(host?.hostId) { mutableStateOf("") }
    var certificateNames by rememberSaveable(host?.hostId) { mutableStateOf("localhost,127.0.0.1") }
    val pickBundle = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        bundleUri = uri?.toString()
        bundleName = uri?.lastPathSegment.orEmpty()
    }
    val pickCertificate = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        certificateUri = uri?.toString()
        certificateName = uri?.lastPathSegment.orEmpty()
    }
    val pickPrivateKey = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        privateKeyUri = uri?.toString()
        privateKeyName = uri?.lastPathSegment.orEmpty()
    }
    val mayContinue = when (step) {
        0 -> source == "official" || (source == "local" && bundleName.isNotBlank()) ||
            (source == "remote" && remoteBundlePath.trim().endsWith(".zip", ignoreCase = true))
        1 -> certificateMode != "custom" ||
            (certificateName.isNotBlank() && (certificateFormat != "pem" || privateKeyName.isNotBlank()))
        else -> false
    }

    // Resize the scroll viewport above the keyboard so TextField focus relocation
    // can keep the edited field visible, including the final certificate names field.
    Column(modifier.fillMaxSize().imePadding()) {
        Column(
            Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = Spacing.lg),
            verticalArrangement = Arrangement.spacedBy(Spacing.md),
        ) {
            Text(stringResource(R.string.ssh_workspace_deploy_step, step + 1), color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (step == 0 && host != null) ServerInstallRecoveryPanel(host.hostId)
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
                        ),
                        value = source,
                        onValueChange = { source = it },
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
                        onValueChange = { mode = it },
                    )
                    if (mode == "linuxSystem") PasswordTextField(sudoPassword, { sudoPassword = it },
                        stringResource(R.string.ssh_workspace_deploy_sudo_password))
                    SelectField(
                        label = stringResource(R.string.ssh_workspace_deploy_file_access),
                        options = listOf(
                            SelectOption("restricted", stringResource(R.string.ssh_workspace_deploy_restricted)),
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
                    if (!mayContinue) Text(stringResource(R.string.ssh_workspace_deploy_certificate_required), color = MaterialTheme.colorScheme.error)
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
                        else -> stringResource(R.string.ssh_workspace_deploy_source_official)
                    })
                    ReviewLine(stringResource(R.string.ssh_workspace_deploy_mode), when (mode) {
                        "linuxUser" -> stringResource(R.string.ssh_workspace_deploy_linux_user)
                        "linuxSystem" -> stringResource(R.string.ssh_workspace_deploy_linux_system)
                        "windowsSystem" -> stringResource(R.string.ssh_workspace_deploy_windows_system)
                        else -> stringResource(R.string.ssh_workspace_deploy_automatic)
                    })
                    ReviewLine(stringResource(R.string.ssh_workspace_deploy_file_access), stringResource(if (fileAccess == "full") R.string.ssh_workspace_deploy_full_access else R.string.ssh_workspace_deploy_restricted))
                    ReviewLine(stringResource(R.string.ssh_workspace_deploy_network), stringResource(if (network == "lan") R.string.ssh_workspace_deploy_lan else R.string.ssh_workspace_deploy_loopback))
                    ReviewLine(stringResource(R.string.ssh_workspace_deploy_certificate), when (certificateMode) {
                        "custom" -> stringResource(R.string.ssh_workspace_deploy_certificate_custom)
                        "selfSigned" -> stringResource(R.string.ssh_workspace_deploy_certificate_self_signed)
                        else -> stringResource(R.string.ssh_workspace_deploy_certificate_none)
                    })
                    Text(stringResource(R.string.ssh_workspace_deploy_source_checks), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    installState.message?.let { Text(stringResource(it), color = MaterialTheme.colorScheme.primary) }
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
                    privateKeyUri?.let(Uri::parse), certificatePassword, certificateNames, sudoPassword)) }
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
