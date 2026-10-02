package app.relaxkonos.mobile.ui.nav

import app.relaxkonos.mobile.ui.common.*
import androidx.compose.runtime.saveable.rememberSaveable
import app.relaxkonos.mobile.ui.theme.Spacing
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.files.FileDetailScreen
import app.relaxkonos.mobile.ui.files.FileMessageBanner
import app.relaxkonos.mobile.ui.files.FileOperationOverlays
import app.relaxkonos.mobile.ui.files.FileTransferCard
import app.relaxkonos.mobile.ui.files.FileUploadCard
import app.relaxkonos.mobile.ui.files.FilesScreen
import app.relaxkonos.mobile.ui.files.FilesViewModel
import app.relaxkonos.mobile.ui.home.HomeScreen
import app.relaxkonos.mobile.ui.manage.tunnels.TunnelsScreen
import app.relaxkonos.mobile.ui.manage.proxy.ProxyScreen
import app.relaxkonos.mobile.ui.manage.certificates.CertificatesScreen
import app.relaxkonos.mobile.ui.manage.ManageScreen
import app.relaxkonos.mobile.ui.manage.ManageViewModel
import app.relaxkonos.mobile.ui.manage.deployments.DeploymentsScreen
import app.relaxkonos.mobile.ui.manage.docker.DockerScreen
import app.relaxkonos.mobile.ui.manage.git.GitScreen
import app.relaxkonos.mobile.ui.manage.guardian.GuardianScreen
import app.relaxkonos.mobile.ui.manage.scripts.ScriptsScreen
import app.relaxkonos.mobile.ui.manage.monitor.MonitorScreen
import app.relaxkonos.mobile.ui.manage.operations.OperationsScreen
import app.relaxkonos.mobile.ui.manage.processes.ProcessesScreen
import app.relaxkonos.mobile.ui.manage.websites.WebsitesScreen
import app.relaxkonos.mobile.ui.more.AboutScreen
import app.relaxkonos.mobile.ui.more.AccountSecurityScreen
import app.relaxkonos.mobile.ui.more.AppearanceScreen
import app.relaxkonos.mobile.ui.more.ConnectionsScreen
import app.relaxkonos.mobile.ui.more.DiagnosticsScreen
import app.relaxkonos.mobile.ui.more.OutboundProxyScreen
import app.relaxkonos.mobile.ui.more.MoreScreen
import app.relaxkonos.mobile.ui.more.ServerInformationScreen
import app.relaxkonos.mobile.ui.terminal.ServerTerminalScreen

/**
 * Maps the navigator's current route to a screen.
 *
 * The Expanded layout does not push sub-pages: `files/detail` becomes a second pane beside the list and
 * the manage and more destinations select a pane. Everything else is a pushed page with a back
 * affordance, which is the Compact and Medium shape (`Shell.Design.md` §3.2, §4.1).
 */
@Composable
fun MobileNavHost(
    container: AppContainer,
    navigator: MobileNavigator,
    session: SessionState.Active,
    layoutState: LayoutState,
    onSignOut: () -> Unit,
    onSwitchLogin: (SavedLogin?) -> Unit,
    modifier: Modifier = Modifier,
) {
    var taskTarget by remember(session) { mutableStateOf<String?>(null) }
    Box(modifier.fillMaxSize()) {
        when (navigator.route) {
            Routes.MANAGE_DEPLOYMENTS, Routes.MANAGE_DEPLOYMENT_DETAIL -> DeploymentsScreen(
                layoutState = layoutState,
                showDetail = navigator.route == Routes.MANAGE_DEPLOYMENT_DETAIL,
                onOpenDetail = { navigator.push(Routes.MANAGE_DEPLOYMENT_DETAIL) },
                onBack = { navigator.pop() },
                initialApplicationId = taskTarget,
            )
            Routes.MANAGE_DOCKER, Routes.MANAGE_DOCKER_RESOURCES, Routes.MANAGE_DOCKER_CONTROL ->
                app.relaxkonos.mobile.ui.manage.docker.DockerWorkspace(
                    initialSection = when (navigator.route) { Routes.MANAGE_DOCKER_RESOURCES -> "containers"; Routes.MANAGE_DOCKER_CONTROL -> "overview"; else -> "overview" },
                    initialStack = taskTarget, onBack = { navigator.pop() },
                    onOpenApplication = { taskTarget = it; navigator.push(Routes.MANAGE_DEPLOYMENT_DETAIL) },
                    onOpenManagedProxy = { taskTarget = null; navigator.push(Routes.MANAGE_PROXY) }, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_GIT -> GitScreen(owner = session, onBack = { navigator.pop() },
                initialBuildId = taskTarget, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_WEBSITES -> WebsitesScreen(onBack = { navigator.pop() },
                initialApplicationId = taskTarget, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_GUARDIAN -> GuardianScreen(owner = session, onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_SCRIPTS -> ScriptsScreen(owner = session, onBack = { navigator.pop() },
                initialTaskId = taskTarget, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_SMB -> app.relaxkonos.mobile.ui.manage.smb.SmbScreen(onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_FIREWALL -> app.relaxkonos.mobile.ui.manage.firewall.FirewallScreen(onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_PROXY -> ProxyScreen(onBack = { navigator.pop() }, initialOperationId = taskTarget, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_TUNNELS -> TunnelsScreen(onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_CERTIFICATES -> CertificatesScreen(onBack = { navigator.pop() }, initialOperationId = taskTarget, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_OPERATIONS -> OperationsScreen(owner = session, onBack = { navigator.pop() },
                startOnAlerts = container.openAlertsOnNextScreen,
                onStartOnAlertsConsumed = container::consumeAlertScreen,
                onOpenDeployment = { taskTarget = it; navigator.push(Routes.MANAGE_DEPLOYMENT_DETAIL) },
                onOpenWebsite = { taskTarget = it; navigator.push(Routes.MANAGE_WEBSITES) },
                onOpenWebServers = { taskTarget = null; navigator.push(Routes.MANAGE_WEBSITES) },
                onOpenCertificates = { taskTarget = it; navigator.push(Routes.MANAGE_CERTIFICATES) },
                onOpenTunnels = { taskTarget = null; navigator.push(Routes.MANAGE_TUNNELS) },
                onOpenDockerResources = { navigator.push(Routes.MANAGE_DOCKER_RESOURCES) },
                onOpenDockerControl = { taskTarget = null; navigator.push(Routes.MANAGE_DOCKER_CONTROL) },
                onOpenSmb = { taskTarget = null; navigator.push(Routes.MANAGE_SMB) },
                onOpenFirewall = { taskTarget = null; navigator.push(Routes.MANAGE_FIREWALL) },
                onOpenProxy = { taskTarget = it; navigator.push(Routes.MANAGE_PROXY) },
                onOpenCompose = { taskTarget = it; navigator.push(Routes.MANAGE_DOCKER) },
                onOpenGit = { taskTarget = null; navigator.push(Routes.MANAGE_GIT) },
                onOpenGitBuild = { taskTarget = it; navigator.push(Routes.MANAGE_GIT) },
                onOpenScript = { taskTarget = it; navigator.push(Routes.MANAGE_SCRIPTS) },
                onOpenDocker = { taskTarget = null; navigator.push(Routes.MANAGE_DOCKER) },
                onOpenGuardian = { taskTarget = null; navigator.push(Routes.MANAGE_GUARDIAN) },
                modifier = Modifier.fillMaxSize())
            Routes.TERMINAL -> ServerTerminalScreen(owner = session, modifier = Modifier.fillMaxSize())
            Routes.FILES, Routes.FILES_DETAIL -> FilesDestination(navigator, layoutState)
            Routes.MANAGE, Routes.MANAGE_MONITOR, Routes.MANAGE_PROCESSES ->
                ManageDestination(navigator, layoutState, clearTaskTarget = { taskTarget = null })
            Routes.MORE,
            Routes.MORE_NETWORK,
            Routes.MORE_ACCOUNT_SECURITY,
            Routes.MORE_CONNECTIONS,
            Routes.MORE_SERVER_INFORMATION,
            Routes.MORE_APPEARANCE,
            Routes.MORE_DIAGNOSTICS,
            Routes.MORE_APPLICATIONS,
            Routes.MORE_HELP,
            Routes.MORE_HOST_SETTINGS,
            Routes.MORE_ABOUT,
            -> MoreDestination(navigator, layoutState, onSignOut, onSwitchLogin, clearTaskTarget = { taskTarget = null }, onOpenManagedProxy = { taskTarget = null; navigator.push(Routes.MANAGE_PROXY) })

            else -> HomeScreen(
                session = session,
                layoutState = layoutState,
                onOpenHelp = { navigator.select(Routes.MORE); navigator.push(Routes.MORE_HELP) },
                modifier = Modifier.fillMaxSize(),
            )
        }
    }
}

/**
 * The files destination.
 *
 * The layout switch happens inside the destination's own chrome, not around it: the message banner
 * and the transfer card are owned here because a download or an upload outlives the page that started
 * it. In the Compact and Medium layouts the list and the detail are alternate routes, so a banner or
 * a progress card living in either one would be gone while the other is shown — which is exactly how
 * a finished download ends up reported on the next navigation instead of when it finishes.
 */
@Composable
private fun FilesDestination(navigator: MobileNavigator, layoutState: LayoutState) {
    val viewModel: FilesViewModel = viewModel()
    Box(Modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize()) {
            FileMessageBanner(viewModel)
            Box(Modifier.weight(1f)) {
                if (layoutState == LayoutState.Expanded) {
                    Row(Modifier.fillMaxSize()) {
                        FilesScreen(viewModel, onOpenDetail = {}, modifier = Modifier.weight(1f))
                        FileDetailScreen(viewModel, onBack = null, modifier = Modifier.weight(1f))
                    }
                } else if (navigator.route == Routes.FILES_DETAIL) {
                    FileDetailScreen(viewModel, onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize())
                } else {
                    FilesScreen(viewModel, onOpenDetail = { navigator.push(Routes.FILES_DETAIL) }, modifier = Modifier.fillMaxSize())
                }
            }
            FileTransferCard(viewModel)
            FileUploadCard(viewModel)
        }
        FileOperationOverlays(viewModel)
    }
}

@Composable
private fun ManageDestination(navigator: MobileNavigator, layoutState: LayoutState, clearTaskTarget: () -> Unit) {
    val viewModel: ManageViewModel = viewModel()

    if (layoutState == LayoutState.Expanded) {
        Row(Modifier.fillMaxSize()) {
            ManageScreen(
                onOpenMonitor = { viewModel.openPane(Routes.MANAGE_MONITOR) },
                onOpenDeployments = { clearTaskTarget(); navigator.push(Routes.MANAGE_DEPLOYMENTS) },
                onOpenDocker = { clearTaskTarget(); navigator.push(Routes.MANAGE_DOCKER) },
                onOpenGit = { clearTaskTarget(); navigator.push(Routes.MANAGE_GIT) },
                onOpenWebsites = { clearTaskTarget(); navigator.push(Routes.MANAGE_WEBSITES) },
            onOpenCertificates = { clearTaskTarget(); navigator.push(Routes.MANAGE_CERTIFICATES) },
                onOpenTunnels = { clearTaskTarget(); navigator.push(Routes.MANAGE_TUNNELS) },
            onOpenSmb = { clearTaskTarget(); navigator.push(Routes.MANAGE_SMB) },
            onOpenFirewall = { clearTaskTarget(); navigator.push(Routes.MANAGE_FIREWALL) },
            onOpenProxy = { clearTaskTarget(); navigator.push(Routes.MANAGE_PROXY) },
                onOpenGuardian = { navigator.push(Routes.MANAGE_GUARDIAN) },
                onOpenScripts = { clearTaskTarget(); navigator.push(Routes.MANAGE_SCRIPTS) },
                onOpenOperations = { navigator.push(Routes.MANAGE_OPERATIONS) },
                modifier = Modifier.weight(1f),
            )
            when (viewModel.expandedPane) {
                Routes.MANAGE_MONITOR -> app.relaxkonos.mobile.ui.manage.TaskManagerWorkspace("performance", null, Modifier.weight(1.2f))
                Routes.MANAGE_PROCESSES -> app.relaxkonos.mobile.ui.manage.TaskManagerWorkspace("processes", null, Modifier.weight(1.2f))
                else -> Text(
                    text = stringResource(R.string.manage_select_domain),
                    modifier = Modifier.weight(1.2f).padding(16.dp),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        return
    }

    when (navigator.route) {
        Routes.MANAGE_MONITOR -> app.relaxkonos.mobile.ui.manage.TaskManagerWorkspace("performance", { navigator.pop() }, Modifier.fillMaxSize())
        Routes.MANAGE_PROCESSES -> app.relaxkonos.mobile.ui.manage.TaskManagerWorkspace("processes", { navigator.pop() }, Modifier.fillMaxSize())
        else -> ManageScreen(
            onOpenMonitor = { navigator.push(Routes.MANAGE_MONITOR) },
            onOpenDeployments = { clearTaskTarget(); navigator.push(Routes.MANAGE_DEPLOYMENTS) },
            onOpenDocker = { clearTaskTarget(); navigator.push(Routes.MANAGE_DOCKER) },
            onOpenGit = { clearTaskTarget(); navigator.push(Routes.MANAGE_GIT) },
            onOpenWebsites = { clearTaskTarget(); navigator.push(Routes.MANAGE_WEBSITES) },
            onOpenCertificates = { clearTaskTarget(); navigator.push(Routes.MANAGE_CERTIFICATES) },
            onOpenTunnels = { clearTaskTarget(); navigator.push(Routes.MANAGE_TUNNELS) },
            onOpenSmb = { clearTaskTarget(); navigator.push(Routes.MANAGE_SMB) },
            onOpenFirewall = { clearTaskTarget(); navigator.push(Routes.MANAGE_FIREWALL) },
            onOpenProxy = { clearTaskTarget(); navigator.push(Routes.MANAGE_PROXY) },
            onOpenGuardian = { navigator.push(Routes.MANAGE_GUARDIAN) },
            onOpenScripts = { clearTaskTarget(); navigator.push(Routes.MANAGE_SCRIPTS) },
            onOpenOperations = { navigator.push(Routes.MANAGE_OPERATIONS) },
            modifier = Modifier.fillMaxSize(),
        )
    }
}

@Composable
private fun MoreDestination(
    navigator: MobileNavigator,
    layoutState: LayoutState,
    onSignOut: () -> Unit,
    onSwitchLogin: (SavedLogin?) -> Unit,
    clearTaskTarget: () -> Unit,
    onOpenManagedProxy: () -> Unit,
) {
    var pane by remember { mutableStateOf<String?>(navigator.route.takeIf { it.startsWith("more/") }) }
    LaunchedEffect(navigator.route, layoutState) {
        if(layoutState == LayoutState.Expanded && navigator.route.startsWith("more/")) pane = navigator.route
    }
    val openFeature: (String) -> Unit = { target ->
        if (target.startsWith("more/")) {
            if(layoutState == LayoutState.Expanded) pane = target else navigator.replaceTop(target)
        } else {
            clearTaskTarget()
            val destination = target.substringBefore('/')
            navigator.select(destination)
            navigator.popToDestinationRoot()
            if(target != destination) navigator.push(target)
        }
    }


    if (layoutState == LayoutState.Expanded) {
        Row(Modifier.fillMaxSize()) {
            MoreScreen(
                onOpenRoute = { pane = it },
                onSignOut = onSignOut,
                onSwitchLogin = { onSwitchLogin(null) },
                modifier = Modifier.weight(1f),
            )
            Box(Modifier.weight(1.2f)) {
                if (pane == null) {
                    EmptyHint(stringResource(R.string.more_select_section), Modifier.padding(16.dp))
                } else {
                    SettingsWorkspace(route = pane!!, onOpenRoute = openFeature, onBack = null, onSwitchLogin = onSwitchLogin, onOpenManagedProxy = onOpenManagedProxy)
                }
            }
        }
        return
    }

    val route = navigator.route
    if (route == Routes.MORE) {
        MoreScreen(
            onOpenRoute = { navigator.push(it) },
            onSignOut = onSignOut,
            onSwitchLogin = { onSwitchLogin(null) },
            modifier = Modifier.fillMaxSize(),
        )
    } else {
        SettingsWorkspace(route = route, onOpenRoute = openFeature, onBack = { navigator.pop() }, onSwitchLogin = onSwitchLogin, onOpenManagedProxy = onOpenManagedProxy)
    }
}

/** One settings page, rendered either as a pushed page ([onBack] non-null) or as a pane. */
@Composable
private fun MorePane(route: String, onOpenRoute: (String) -> Unit, onBack: (() -> Unit)?, onSwitchLogin: (SavedLogin?) -> Unit, onOpenManagedProxy: () -> Unit) {
    val moreContainer = app.relaxkonos.mobile.ui.common.appContainer()
    when (route) {
        Routes.MORE_ACCOUNT_SECURITY -> AccountSecurityScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_CONNECTIONS -> ConnectionsScreen(onBack = onBack, onSwitchLogin = onSwitchLogin, modifier = Modifier.fillMaxSize())
        Routes.MORE_SERVER_INFORMATION -> ServerInformationScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_NETWORK -> OutboundProxyScreen(onBack = onBack, onOpenManagedProxy = onOpenManagedProxy, modifier = Modifier.fillMaxSize())
        Routes.MORE_APPEARANCE -> AppearanceScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_DIAGNOSTICS -> DiagnosticsScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_APPLICATIONS -> app.relaxkonos.mobile.ui.more.MobileApplicationsScreen(onBack = onBack,
            onOpenRoute = onOpenRoute, modifier = Modifier.fillMaxSize())
        Routes.MORE_HELP -> app.relaxkonos.mobile.ui.more.HelpScreen(onBack = onBack, onOpenRoute = onOpenRoute,
            onOpenServerCenter = { moreContainer.serverCenter.open() }, modifier = Modifier.fillMaxSize())
        Routes.MORE_HOST_SETTINGS -> app.relaxkonos.mobile.ui.more.HostSettingsScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_ABOUT -> AboutScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        else -> EmptyHint(stringResource(R.string.more_select_section))
    }
}

@Composable
private fun SettingsWorkspace(route: String, onOpenRoute: (String) -> Unit, onBack: (() -> Unit)?,
    onSwitchLogin: (SavedLogin?) -> Unit, onOpenManagedProxy: () -> Unit) {
    val pages = listOf(
        WorkspaceDestination(Routes.MORE_APPEARANCE, R.string.more_appearance),
        WorkspaceDestination(Routes.MORE_APPLICATIONS, R.string.mobile_apps_title),
        WorkspaceDestination(Routes.MORE_CONNECTIONS, R.string.more_connections),
        WorkspaceDestination(Routes.MORE_ACCOUNT_SECURITY, R.string.more_account_security),
        WorkspaceDestination(Routes.MORE_HOST_SETTINGS, R.string.host_settings_title),
        WorkspaceDestination(Routes.MORE_SERVER_INFORMATION, R.string.more_server_information),
        WorkspaceDestination(Routes.MORE_NETWORK, R.string.workspace_proxy),
        WorkspaceDestination(Routes.MORE_DIAGNOSTICS, R.string.more_diagnostics),
        WorkspaceDestination(Routes.MORE_HELP, R.string.help_title),
        WorkspaceDestination(Routes.MORE_ABOUT, R.string.more_about),
    ).filter { it.id != Routes.MORE_NETWORK || app.relaxkonos.mobile.core.net.ServerCapabilities.DOCKER in app.relaxkonos.mobile.ui.common.appContainer().capabilities }
    Column(Modifier.fillMaxSize()) {
        Text(stringResource(when (route) {
            Routes.MORE_HOST_SETTINGS, Routes.MORE_SERVER_INFORMATION, Routes.MORE_NETWORK -> R.string.workspace_scope_host
            Routes.MORE_CONNECTIONS, Routes.MORE_DIAGNOSTICS -> R.string.workspace_scope_connection
            else -> R.string.workspace_scope_android
        }), modifier = Modifier.padding(Spacing.sm))
        WorkspaceNavigation(pages, route, onOpenRoute)
        androidx.compose.runtime.key(appContainer().activeSession) { Box(Modifier.weight(1f)) {
            pages.forEach { page -> androidx.compose.runtime.key(page.id) {
                WorkspaceSection(route == page.id, Modifier.fillMaxSize()) {
                    MorePane(page.id, onOpenRoute, onBack.takeIf { route == page.id }, onSwitchLogin, onOpenManagedProxy)
                }
            } }
        } }
    }
}
