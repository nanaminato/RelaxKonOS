package app.relaxkonos.mobile.ui.servercenter

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.ServerHostTarget
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

/** SSH keeps the same compact navigation shape as an authenticated RelaxKonOS session. */
@Composable
fun SshWorkspaceScreen(hostId: String, onClose: () -> Unit) {
    var page by rememberSaveable(hostId) { mutableIntStateOf(0) }
    val host = (LocalContext.current.applicationContext as RelaxKonApplication)
        .container.serverCenter.hosts().firstOrNull { it.hostId == hostId }
    val title = stringResource(if (page == 0) R.string.ssh_files_title else R.string.ssh_workspace_deploy)
    Scaffold(
        containerColor = androidx.compose.ui.graphics.Color.Transparent,
        bottomBar = {
            NavigationBar {
                NavigationBarItem(
                    selected = page == 0,
                    onClick = { page = 0 },
                    icon = { DesktopIcon(DesktopIcons.navFiles, size = 26.dp) },
                    label = { Text(stringResource(R.string.ssh_files_title)) },
                )
                NavigationBarItem(
                    selected = page == 1,
                    onClick = { page = 1 },
                    icon = { DesktopIcon(DesktopIcons.deployments, size = 26.dp) },
                    label = { Text(stringResource(R.string.ssh_workspace_deploy)) },
                )
            }
        },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            ScreenHeader(
                title = title,
                subtitle = stringResource(R.string.ssh_workspace_subtitle, host?.displayName ?: hostId),
                onBack = onClose,
                modifier = Modifier.padding(Spacing.lg),
            )
            if (page == 0) SshFilesScreen(hostId) else DeploymentSetupScreen(host)
        }
    }
}

/** Matches the desktop source → mode → review flow. Execution awaits packaged trusted assets. */
@Composable
private fun DeploymentSetupScreen(host: ServerHostTarget?) {
    var step by rememberSaveable(host?.hostId) { mutableIntStateOf(0) }
    var source by rememberSaveable(host?.hostId) { mutableStateOf("official") }
    var bundleName by rememberSaveable(host?.hostId) { mutableStateOf("") }
    var remoteBundlePath by rememberSaveable(host?.hostId) { mutableStateOf("") }
    var mode by rememberSaveable(host?.hostId) { mutableStateOf("automatic") }
    var fileAccess by rememberSaveable(host?.hostId) { mutableStateOf("restricted") }
    var network by rememberSaveable(host?.hostId) { mutableStateOf("loopback") }
    var certificateMode by rememberSaveable(host?.hostId) { mutableStateOf("none") }
    var certificateFormat by rememberSaveable(host?.hostId) { mutableStateOf("pfx") }
    var certificateName by rememberSaveable(host?.hostId) { mutableStateOf("") }
    var privateKeyName by rememberSaveable(host?.hostId) { mutableStateOf("") }
    var certificatePassword by remember(host?.hostId) { mutableStateOf("") }
    var certificateNames by rememberSaveable(host?.hostId) { mutableStateOf("localhost,127.0.0.1") }
    val pickBundle = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        bundleName = uri?.lastPathSegment.orEmpty()
    }
    val pickCertificate = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        certificateName = uri?.lastPathSegment.orEmpty()
    }
    val pickPrivateKey = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        privateKeyName = uri?.lastPathSegment.orEmpty()
    }
    val mayContinue = when (step) {
        0 -> source == "official" || (source == "local" && bundleName.isNotBlank()) ||
            (source == "remote" && remoteBundlePath.trim().endsWith(".zip", ignoreCase = true))
        1 -> certificateMode != "custom" ||
            (certificateName.isNotBlank() && (certificateFormat != "pem" || privateKeyName.isNotBlank()))
        else -> false
    }

    Column(Modifier.fillMaxSize()) {
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
                    FilterChip(source == "official", { source = "official" }, { Text(stringResource(R.string.ssh_workspace_deploy_source_official)) })
                    FilterChip(source == "local", { source = "local" }, { Text(stringResource(R.string.ssh_workspace_deploy_source_local)) })
                    FilterChip(source == "remote", { source = "remote" }, { Text(stringResource(R.string.ssh_workspace_deploy_source_remote)) })
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
                    }
                    if (!mayContinue) Text(stringResource(R.string.ssh_workspace_deploy_bundle_required), color = MaterialTheme.colorScheme.error)
                }

                1 -> SectionCard(
                    title = stringResource(R.string.ssh_workspace_deploy_step_mode),
                    subtitle = stringResource(R.string.ssh_workspace_deploy_draft),
                    leading = DesktopIcons.deployments,
                ) {
                    Text(stringResource(R.string.ssh_workspace_deploy_mode), style = MaterialTheme.typography.labelLarge)
                    FilterChip(mode == "automatic", { mode = "automatic" }, { Text(stringResource(R.string.ssh_workspace_deploy_automatic)) })
                    FilterChip(mode == "linuxUser", { mode = "linuxUser" }, { Text(stringResource(R.string.ssh_workspace_deploy_linux_user)) })
                    FilterChip(mode == "linuxSystem", { mode = "linuxSystem" }, { Text(stringResource(R.string.ssh_workspace_deploy_linux_system)) })
                    FilterChip(mode == "windowsSystem", { mode = "windowsSystem" }, { Text(stringResource(R.string.ssh_workspace_deploy_windows_system)) })
                    Text(stringResource(R.string.ssh_workspace_deploy_file_access), style = MaterialTheme.typography.labelLarge)
                    FilterChip(fileAccess == "restricted", { fileAccess = "restricted" }, { Text(stringResource(R.string.ssh_workspace_deploy_restricted)) })
                    FilterChip(fileAccess == "full", { fileAccess = "full" }, { Text(stringResource(R.string.ssh_workspace_deploy_full_access)) })
                    Text(stringResource(R.string.ssh_workspace_deploy_network), style = MaterialTheme.typography.labelLarge)
                    FilterChip(network == "loopback", { network = "loopback" }, { Text(stringResource(R.string.ssh_workspace_deploy_loopback)) })
                    FilterChip(network == "lan", { network = "lan" }, { Text(stringResource(R.string.ssh_workspace_deploy_lan)) })
                    Text(stringResource(R.string.ssh_workspace_deploy_certificate), style = MaterialTheme.typography.labelLarge)
                    FilterChip(certificateMode == "none", { certificateMode = "none" }, { Text(stringResource(R.string.ssh_workspace_deploy_certificate_none)) })
                    FilterChip(certificateMode == "custom", { certificateMode = "custom" }, { Text(stringResource(R.string.ssh_workspace_deploy_certificate_custom)) })
                    FilterChip(certificateMode == "selfSigned", { certificateMode = "selfSigned" }, { Text(stringResource(R.string.ssh_workspace_deploy_certificate_self_signed)) })
                    if (certificateMode == "custom") {
                        Text(stringResource(R.string.ssh_workspace_deploy_certificate_format), style = MaterialTheme.typography.labelLarge)
                        FilterChip(certificateFormat == "pfx", { certificateFormat = "pfx" }, { Text(stringResource(R.string.ssh_workspace_deploy_certificate_pfx)) })
                        FilterChip(certificateFormat == "pem", { certificateFormat = "pem" }, { Text(stringResource(R.string.ssh_workspace_deploy_certificate_pem)) })
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
                    Text(stringResource(R.string.ssh_workspace_deploy_unavailable), color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        Row(
            Modifier.fillMaxWidth().padding(Spacing.lg),
            horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
        ) {
            if (step > 0) OutlinedButton(onClick = { step-- }, modifier = Modifier.weight(1f)) {
                Text(stringResource(R.string.common_back))
            }
            if (step < 2) Button(onClick = { step++ }, enabled = mayContinue, modifier = Modifier.weight(1f)) {
                Text(stringResource(R.string.ssh_workspace_deploy_next))
            } else Button(onClick = {}, enabled = false, modifier = Modifier.weight(1f)) {
                Text(stringResource(R.string.ssh_workspace_deploy_install))
            }
        }
    }
}

@Composable
private fun ReviewLine(label: String, value: String) {
    Column(Modifier.fillMaxWidth()) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}
