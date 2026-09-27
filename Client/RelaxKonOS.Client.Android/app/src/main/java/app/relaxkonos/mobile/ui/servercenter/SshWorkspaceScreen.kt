package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.ScreenHeader
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
            0 -> SshFilesScreen(hostId, onClose, showHeader = false)
            else -> DeploymentUnavailable()
        }
    }
}

/**
 * The protocol client exists, but this Android package deliberately ships no pinned launcher,
 * verifier binary or release trust root.  Offering an active installer without those materials
 * would be a downgrade from the desktop's verification guarantees.
 */
@Composable
private fun DeploymentUnavailable() {
    Card(Modifier.padding(Spacing.lg)) {
        Column(Modifier.padding(Spacing.md)) {
            Text(stringResource(R.string.ssh_workspace_deploy), style = MaterialTheme.typography.titleMedium)
            Text(stringResource(R.string.ssh_workspace_deploy_unavailable), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
