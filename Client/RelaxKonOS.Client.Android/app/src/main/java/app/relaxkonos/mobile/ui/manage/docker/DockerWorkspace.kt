package app.relaxkonos.mobile.ui.manage.docker

import androidx.compose.foundation.layout.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.DockerResourceKind
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.more.OutboundProxyScreen

@Composable
fun DockerWorkspace(initialSection: String, initialStack: String?, onBack: () -> Unit,
    onOpenApplication: (String) -> Unit, onOpenManagedProxy: () -> Unit, modifier: Modifier = Modifier) {
    val owner = appContainer().activeSession
    val control: DockerControlViewModel = viewModel()
    var requestedStack by rememberSaveable(owner) { mutableStateOf(initialStack) }
    var section by rememberSaveable(owner) { mutableStateOf(initialSection) }
    LaunchedEffect(initialSection, initialStack) {
        requestedStack = initialStack
        section = if (initialStack != null) "compose" else initialSection
    }
    val pages = listOf(
        WorkspaceDestination("overview", R.string.workspace_overview),
        WorkspaceDestination("containers", R.string.workspace_containers),
        WorkspaceDestination("compose", R.string.workspace_compose),
        WorkspaceDestination("images", R.string.workspace_images),
        WorkspaceDestination("mirrors", R.string.workspace_mirrors),
        WorkspaceDestination("proxy", R.string.workspace_proxy),
        WorkspaceDestination("networks", R.string.workspace_networks),
        WorkspaceDestination("volumes", R.string.workspace_volumes),
        WorkspaceDestination("records", R.string.workspace_records),
    )
    val screenTitle = stringResource(when (section) {
        "overview", "compose" -> R.string.docker_title
        "mirrors" -> R.string.workspace_mirrors
        "proxy" -> R.string.proxy_title
        else -> R.string.docker_resources_title
    })
    WorkspaceFrame(stringResource(R.string.docker_title), screenTitle = screenTitle,
        pages = pages, selected = section, onSelect = { section = it }, onBack = onBack, modifier = modifier) {
        val state = control.state
        if (state.owner === owner && section != "records" &&
            (state.pending.isNotEmpty() || state.resourcePending || state.pendingInstallation || state.installation?.state?.active == true)) {
            TextButton(onClick = { section = "records" }) {
                Text(stringResource(R.string.workspace_operations) + " · " +
                    (state.installation?.operationId ?: stringResource(R.string.docker_control_pending)))
            }
        }
        key(owner) { Box(Modifier.weight(1f)) {
            WorkspaceSection(section == "overview", Modifier.fillMaxSize()) {
                DockerControlScreen(onBack, active = section == "overview")
            }
            WorkspaceSection(section == "mirrors", Modifier.fillMaxSize()) {
                DockerControlScreen(onBack, mirrorsOnly = true, active = section == "mirrors")
            }
            WorkspaceSection(section == "records", Modifier.fillMaxSize()) {
                DockerControlScreen(onBack, recordsOnly = true, active = section == "records")
            }
            WorkspaceSection(section == "compose", Modifier.fillMaxSize()) {
                DockerScreen(onBack, initialStackName = requestedStack, section = "compose")
            }
            DockerResourceKind.entries.forEach { kind ->
                val id = kind.name.lowercase()
                key(kind) { WorkspaceSection(section == id, Modifier.fillMaxSize()) {
                    DockerResourceScreen(onBack, { section = "records" }, { stack -> requestedStack = stack; section = "compose" },
                        onOpenApplication, resourceKind = kind, active = section == id)
                } }
            }
            WorkspaceSection(section == "proxy", Modifier.fillMaxSize()) {
                OutboundProxyScreen(onBack = onBack.takeIf { section == "proxy" }, onOpenManagedProxy = onOpenManagedProxy)
            }
        } }
    }
}
