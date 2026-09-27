package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.PasswordTextField
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

/** SSH gets the same compact bottom-navigation shape as an authenticated RelaxKonOS session. */
@Composable
fun SshWorkspaceScreen(hostId: String, onClose: () -> Unit) {
    var page by rememberSaveable(hostId) { mutableIntStateOf(0) }
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
                subtitle = stringResource(R.string.ssh_workspace_subtitle, hostId),
                onBack = onClose,
                modifier = Modifier.padding(Spacing.lg),
            )
            if (page == 0) SshFilesScreen(hostId) else DeploymentSetupScreen()
        }
    }
}

/** This draft collects desktop-equivalent constrained options but never submits an unverified release. */
@Composable
private fun DeploymentSetupScreen() {
    var mode by rememberSaveable { mutableStateOf("linuxSystem") }
    var network by rememberSaveable { mutableStateOf("loopback") }
    var source by rememberSaveable { mutableStateOf("official") }
    var fileAccess by rememberSaveable { mutableStateOf("restricted") }
    var certificateMode by rememberSaveable { mutableStateOf("none") }
    var certificateFormat by rememberSaveable { mutableStateOf("pfx") }
    var port by rememberSaveable { mutableStateOf("5127") }
    var version by rememberSaveable { mutableStateOf("") }
    var bundleName by rememberSaveable { mutableStateOf("") }
    var remoteBundlePath by rememberSaveable { mutableStateOf("") }
    var certificateName by rememberSaveable { mutableStateOf("") }
    var privateKeyName by rememberSaveable { mutableStateOf("") }
    var certificatePassword by rememberSaveable { mutableStateOf("") }
    var certificateNames by rememberSaveable { mutableStateOf("localhost,127.0.0.1") }
    val pickBundle = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri -> bundleName = uri?.lastPathSegment.orEmpty() }
    val pickCertificate = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri -> certificateName = uri?.lastPathSegment.orEmpty() }
    val pickPrivateKey = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri -> privateKeyName = uri?.lastPathSegment.orEmpty() }
    Column(Modifier.padding(Spacing.lg).verticalScroll(rememberScrollState())) {
        SectionCard(
            title = stringResource(R.string.ssh_workspace_deploy),
            subtitle = stringResource(R.string.ssh_workspace_deploy_draft),
            leading = DesktopIcons.deployments,
        ) {
            Text(stringResource(R.string.ssh_workspace_deploy_source), style = MaterialTheme.typography.labelLarge)
            FilterChip(source == "official", { source = "official" }, { Text(stringResource(R.string.ssh_workspace_deploy_source_official)) })
            FilterChip(source == "local", { source = "local" }, { Text(stringResource(R.string.ssh_workspace_deploy_source_local)) })
            FilterChip(source == "remote", { source = "remote" }, { Text(stringResource(R.string.ssh_workspace_deploy_source_remote)) })
            if (source == "local") {
                OutlinedButton({ pickBundle.launch(arrayOf("application/zip", "application/octet-stream")) }) { Text(stringResource(R.string.ssh_workspace_deploy_choose_bundle)) }
                if (bundleName.isNotBlank()) Text(bundleName, style = MaterialTheme.typography.bodySmall)
            }
            if (source == "remote") OutlinedTextField(value = remoteBundlePath, onValueChange = { remoteBundlePath = it }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ssh_workspace_deploy_remote_bundle_path)) }, singleLine = true)
            Text(stringResource(R.string.ssh_workspace_deploy_mode), style = MaterialTheme.typography.labelLarge)
            FilterChip(mode == "linuxSystem", { mode = "linuxSystem" }, { Text(stringResource(R.string.ssh_workspace_deploy_linux_system)) })
            FilterChip(mode == "linuxUser", { mode = "linuxUser" }, { Text(stringResource(R.string.ssh_workspace_deploy_linux_user)) })
            FilterChip(mode == "windowsSystem", { mode = "windowsSystem" }, { Text(stringResource(R.string.ssh_workspace_deploy_windows_system)) })
            Text(stringResource(R.string.ssh_workspace_deploy_network), style = MaterialTheme.typography.labelLarge)
            FilterChip(network == "loopback", { network = "loopback" }, { Text(stringResource(R.string.ssh_workspace_deploy_loopback)) })
            FilterChip(network == "lan", { network = "lan" }, { Text(stringResource(R.string.ssh_workspace_deploy_lan)) })
            FilterChip(network == "reverseProxy", { network = "reverseProxy" }, { Text(stringResource(R.string.ssh_workspace_deploy_proxy)) })
            Text(stringResource(R.string.ssh_workspace_deploy_file_access), style = MaterialTheme.typography.labelLarge)
            FilterChip(fileAccess == "restricted", { fileAccess = "restricted" }, { Text(stringResource(R.string.ssh_workspace_deploy_restricted)) })
            FilterChip(fileAccess == "full", { fileAccess = "full" }, { Text(stringResource(R.string.ssh_workspace_deploy_full_access)) })
            OutlinedTextField(value = port, onValueChange = { port = it }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ssh_workspace_deploy_port)) }, singleLine = true)
            OutlinedTextField(value = version, onValueChange = { version = it }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ssh_workspace_deploy_version)) }, singleLine = true)
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
                OutlinedTextField(value = certificateNames, onValueChange = { certificateNames = it }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ssh_workspace_deploy_certificate_names)) })
            }
            Text(stringResource(R.string.ssh_workspace_deploy_unavailable), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
