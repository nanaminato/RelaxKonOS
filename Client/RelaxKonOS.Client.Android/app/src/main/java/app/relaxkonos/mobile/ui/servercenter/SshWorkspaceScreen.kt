package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.theme.Spacing

/** The post-verification SSH destination, parallel to the authenticated RelaxKonOS shell. */
@Composable
fun SshWorkspaceScreen(hostId: String, onClose: () -> Unit) {
    var page by rememberSaveable(hostId) { mutableIntStateOf(0) }
    Column(Modifier.fillMaxSize()) {
        ScreenHeader(
            title = stringResource(R.string.ssh_workspace_title),
            subtitle = stringResource(R.string.ssh_workspace_subtitle, hostId),
            onBack = onClose,
            modifier = Modifier.padding(Spacing.lg),
        )
        TabRow(selectedTabIndex = page) {
            Tab(page == 0, { page = 0 }, text = { Text(stringResource(R.string.ssh_files_title)) })
            Tab(page == 1, { page = 1 }, text = { Text(stringResource(R.string.ssh_workspace_deploy)) })
        }
        when (page) {
            0 -> SshFilesScreen(hostId)
            else -> DeploymentSetupScreen()
        }
    }
}

/**
 * This is intentionally a draft-only surface until the release assets are packaged. It collects the
 * same constrained deployment options as desktop, but cannot submit an unverified package.
 */
@Composable
private fun DeploymentSetupScreen() {
    var mode by rememberSaveable { mutableStateOf("linuxSystem") }
    var network by rememberSaveable { mutableStateOf("loopback") }
    var port by rememberSaveable { mutableStateOf("5127") }
    var version by rememberSaveable { mutableStateOf("") }
    Column(Modifier.padding(Spacing.lg)) {
        SectionCard(
            title = stringResource(R.string.ssh_workspace_deploy),
            subtitle = stringResource(R.string.ssh_workspace_deploy_draft),
            leading = app.relaxkonos.mobile.ui.icons.DesktopIcons.deployments,
        ) {
            Text(stringResource(R.string.ssh_workspace_deploy_mode), style = MaterialTheme.typography.labelLarge)
            FilterChip(mode == "linuxSystem", { mode = "linuxSystem" }, { Text(stringResource(R.string.ssh_workspace_deploy_linux_system)) })
            FilterChip(mode == "linuxUser", { mode = "linuxUser" }, { Text(stringResource(R.string.ssh_workspace_deploy_linux_user)) })
            FilterChip(mode == "windowsSystem", { mode = "windowsSystem" }, { Text(stringResource(R.string.ssh_workspace_deploy_windows_system)) })
            Text(stringResource(R.string.ssh_workspace_deploy_network), style = MaterialTheme.typography.labelLarge)
            FilterChip(network == "loopback", { network = "loopback" }, { Text(stringResource(R.string.ssh_workspace_deploy_loopback)) })
            FilterChip(network == "lan", { network = "lan" }, { Text(stringResource(R.string.ssh_workspace_deploy_lan)) })
            FilterChip(network == "reverseProxy", { network = "reverseProxy" }, { Text(stringResource(R.string.ssh_workspace_deploy_proxy)) })
            OutlinedTextField(value = port, onValueChange = { port = it }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ssh_workspace_deploy_port)) }, singleLine = true)
            OutlinedTextField(value = version, onValueChange = { version = it }, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.ssh_workspace_deploy_version)) }, singleLine = true)
            Text(stringResource(R.string.ssh_workspace_deploy_unavailable), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
