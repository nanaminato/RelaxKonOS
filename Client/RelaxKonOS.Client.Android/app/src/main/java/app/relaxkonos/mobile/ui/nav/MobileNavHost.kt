package app.relaxkonos.mobile.ui.nav

import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.files.FileDetailScreen
import app.relaxkonos.mobile.ui.files.FileActionFeedback
import app.relaxkonos.mobile.ui.files.FileOperationOverlays
import app.relaxkonos.mobile.ui.files.FileTransferCard
import app.relaxkonos.mobile.ui.files.FileUploadCard
import app.relaxkonos.mobile.ui.files.FilesScreen
import app.relaxkonos.mobile.ui.files.FilesViewModel
import app.relaxkonos.mobile.ui.home.HomeScreen
import app.relaxkonos.mobile.ui.manage.tunnels.TunnelsScreen
import app.relaxkonos.mobile.ui.manage.proxy.ProxyScreen
import app.relaxkonos.mobile.ui.manage.certificates.CertificatesScreen
import app.relaxkonos.mobile.ui.manage.components.AppComponentDependencies
import app.relaxkonos.mobile.ui.manage.components.ComponentCatalog
import app.relaxkonos.mobile.ui.manage.components.ComponentDependencyPrompt
import app.relaxkonos.mobile.ui.manage.components.ComponentDependencyVerdict
import app.relaxkonos.mobile.ui.manage.components.componentVerdict
import app.relaxkonos.mobile.ui.manage.components.dependencyVerdict
import app.relaxkonos.mobile.ui.manage.components.readComponentDependencies
import app.relaxkonos.mobile.ui.manage.ManageScreen
import app.relaxkonos.mobile.ui.manage.ManageViewModel
import app.relaxkonos.mobile.ui.manage.deployments.DeploymentsScreen
import app.relaxkonos.mobile.ui.manage.docker.DockerScreen
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
 * Expanded renders file details and management categories as panes. More keeps a settings route
 * across layouts: a right pane in Expanded and a page with a back affordance in Compact/Medium
 * (`Shell.Design.md` §3.2, §4.1).
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
    // Component-backed applications ask once per open, using the same state route the component center
    // reads. Only a measured absence or a pending transaction blocks; an unreadable answer does not.
    var dependencyPrompt by remember(session) { mutableStateOf<ComponentDependencyPrompt?>(null) }
    LaunchedEffect(session, navigator.route) {
        val componentIds = AppComponentDependencies.componentsFor(navigator.route) ?: return@LaunchedEffect
        val rows = readComponentDependencies(session, container.components, componentIds)
        val verdict = dependencyVerdict(rows)
        if (verdict == ComponentDependencyVerdict.Missing || verdict == ComponentDependencyVerdict.Recovery
            || verdict == ComponentDependencyVerdict.Busy) {
            dependencyPrompt = ComponentDependencyPrompt(verdict, rows)
        }
    }
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
                    onOpenManagedProxy = { taskTarget = null; navigator.push(Routes.MANAGE_PROXY) }, modifier = Modifier.fillMaxSize(), onOpenComponents = { navigator.push(Routes.MANAGE_COMPONENTS) })
            Routes.MANAGE_WEBSITES -> WebsitesScreen(onBack = { navigator.pop() },
                initialApplicationId = taskTarget, modifier = Modifier.fillMaxSize(), onOpenComponents = { navigator.push(Routes.MANAGE_COMPONENTS) })
            Routes.MANAGE_GUARDIAN -> GuardianScreen(owner = session, onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_SCRIPTS -> ScriptsScreen(owner = session, onBack = { navigator.pop() },
                initialTaskId = taskTarget, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_SMB -> app.relaxkonos.mobile.ui.manage.smb.SmbScreen(onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize(), onOpenComponents = { navigator.push(Routes.MANAGE_COMPONENTS) })
            Routes.MANAGE_FIREWALL -> app.relaxkonos.mobile.ui.manage.firewall.FirewallScreen(onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_TAILSCALE -> app.relaxkonos.mobile.ui.manage.tailscale.TailscaleScreen(onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize(), onOpenComponents = { navigator.push(Routes.MANAGE_COMPONENTS) })
            Routes.MANAGE_SQUID -> app.relaxkonos.mobile.ui.manage.squid.SquidScreen(onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize(), onOpenComponents = { navigator.push(Routes.MANAGE_COMPONENTS) })
            Routes.MANAGE_PROXY -> ProxyScreen(onBack = { navigator.pop() }, initialOperationId = taskTarget, modifier = Modifier.fillMaxSize(), onOpenComponents = { navigator.push(Routes.MANAGE_COMPONENTS) })
            Routes.MANAGE_PROXY_SERVER -> app.relaxkonos.mobile.ui.manage.proxyserver.ProxyServerScreen(
                onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize(), onOpenComponents = { navigator.push(Routes.MANAGE_COMPONENTS) })
            Routes.MANAGE_TUNNELS -> TunnelsScreen(onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize(), onOpenComponents = { navigator.push(Routes.MANAGE_COMPONENTS) })
            Routes.MANAGE_CERTIFICATES -> CertificatesScreen(onBack = { navigator.pop() }, active = true, initialOperationId = taskTarget, modifier = Modifier.fillMaxSize())
            Routes.MANAGE_COMPONENTS -> app.relaxkonos.mobile.ui.manage.components.ComponentCenterScreen(
                owner = session, onBack = { navigator.pop() },
                onOpenRoute = { taskTarget = null; navigator.push(it) }, modifier = Modifier.fillMaxSize())
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
                onOpenScript = { taskTarget = it; navigator.push(Routes.MANAGE_SCRIPTS) },
                onOpenDocker = { taskTarget = null; navigator.push(Routes.MANAGE_DOCKER) },
                onOpenGuardian = { taskTarget = null; navigator.push(Routes.MANAGE_GUARDIAN) },
                modifier = Modifier.fillMaxSize())
            Routes.TERMINAL -> ServerTerminalScreen(owner = session, modifier = Modifier.fillMaxSize())
            Routes.FILES, Routes.FILES_DETAIL -> FilesDestination(navigator, layoutState)
            Routes.MANAGE, Routes.MANAGE_MONITOR, Routes.MANAGE_PROCESSES ->
                ManageDestination(navigator, clearTaskTarget = { taskTarget = null })
            Routes.MORE,
            Routes.MORE_NETWORK,
            Routes.MORE_ACCOUNT_SECURITY,
            Routes.MORE_CONNECTIONS,
            Routes.MORE_SERVER_INFORMATION,
            Routes.MORE_APPEARANCE,
            Routes.MORE_DIAGNOSTICS,
            Routes.MORE_APPLICATIONS,
            Routes.MORE_HELP,
            Routes.MORE_SERVER_HTTPS,
            Routes.MORE_HOST_SETTINGS,
            Routes.MORE_ABOUT,
            -> MoreDestination(navigator, layoutState, onSignOut, onSwitchLogin, clearTaskTarget = { taskTarget = null }, onOpenManagedProxy = { taskTarget = null; navigator.push(Routes.MANAGE_PROXY) })

            else -> HomeScreen(
                session = session,
                onOpenHelp = { navigator.select(Routes.MORE); navigator.push(Routes.MORE_HELP) },
                onOpenRoute = { taskTarget = null; if (it == Routes.FILES) navigator.select(it) else navigator.push(it) },
                onSwitchLogin = { onSwitchLogin(null) },
                modifier = Modifier.fillMaxSize(),
            )
        }
    }
    dependencyPrompt?.let { prompt -> ComponentDependencyAlert(prompt, navigator, onResolved = { dependencyPrompt = null }) }
}

/**
 * The entry prompt. "Cancel" never opens the application: it returns to the place the user came from,
 * while the actionable state navigates to the one surface that can change it.
 */
@Composable
private fun ComponentDependencyAlert(prompt: ComponentDependencyPrompt, navigator: MobileNavigator,
    onResolved: () -> Unit) {
    val context = LocalContext.current
    val missing = prompt.verdict == ComponentDependencyVerdict.Missing
    val names = prompt.rows.filter { componentVerdict(it) == prompt.verdict }
        .mapNotNull { ComponentCatalog.definition(it.componentId)?.title }
        .map { context.getString(it) }
        .ifEmpty { listOf(context.getString(R.string.components_title)) }
        .joinToString(", ")
    androidx.compose.material3.AlertDialog(
        onDismissRequest = { },
        title = {
            Text(stringResource(if (missing) R.string.components_launch_missing_title else R.string.components_launch_blocked_title))
        },
        text = {
            Text(stringResource(when {
                missing -> R.string.components_launch_missing_message
                prompt.verdict == ComponentDependencyVerdict.Recovery -> R.string.components_launch_recovery_message
                else -> R.string.components_launch_busy_message
            }, names))
        },
        confirmButton = {
            androidx.compose.material3.TextButton(onClick = {
                onResolved()
                navigator.push(if (missing) Routes.MANAGE_COMPONENTS else Routes.MANAGE_OPERATIONS)
            }) {
                Text(stringResource(if (missing) R.string.components_open_center else R.string.components_recovery_open))
            }
        },
        dismissButton = {
            androidx.compose.material3.TextButton(onClick = {
                onResolved()
                if (!navigator.isAtDestinationRoot) navigator.pop()
            }) { Text(stringResource(R.string.common_cancel)) }
        },
    )
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
            FileActionFeedback(viewModel)
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
private fun ManageDestination(navigator: MobileNavigator, clearTaskTarget: () -> Unit) {
    when (navigator.route) {
        Routes.MANAGE_MONITOR -> app.relaxkonos.mobile.ui.manage.TaskManagerWorkspace("performance", { navigator.pop() }, Modifier.fillMaxSize())
        Routes.MANAGE_PROCESSES -> app.relaxkonos.mobile.ui.manage.TaskManagerWorkspace("processes", { navigator.pop() }, Modifier.fillMaxSize())
        else -> ManageScreen(onOpenRoute = { route ->
            clearTaskTarget()
            if (route == Routes.FILES) navigator.select(route) else navigator.push(route)
        }, modifier = Modifier.fillMaxSize())
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
    val route = navigator.route
    val openFeature: (String) -> Unit = { target ->
        if (target.startsWith("more/")) {
            navigator.openSettings(target)
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
                onOpenRoute = openFeature,
                onSignOut = onSignOut,
                onSwitchLogin = { onSwitchLogin(null) },
                modifier = Modifier.weight(1f),
            )
            Box(Modifier.weight(1.2f)) {
                if (route == Routes.MORE) {
                    EmptyHint(stringResource(R.string.more_select_section), Modifier.padding(16.dp))
                } else {
                    SettingsWorkspace(route = route, onOpenRoute = openFeature,
                        onBack = if (route == Routes.MORE_SERVER_HTTPS) ({ navigator.pop(); Unit }) else null,
                        onSwitchLogin = onSwitchLogin, onOpenManagedProxy = onOpenManagedProxy)
                }
            }
        }
        return
    }

    if (route == Routes.MORE) {
        MoreScreen(
            onOpenRoute = openFeature,
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
private fun MorePane(route: String, active: Boolean, onOpenRoute: (String) -> Unit, onBack: (() -> Unit)?, onSwitchLogin: (SavedLogin?) -> Unit, onOpenManagedProxy: () -> Unit) {
    val moreContainer = app.relaxkonos.mobile.ui.common.appContainer()
    when (route) {
        Routes.MORE_ACCOUNT_SECURITY -> AccountSecurityScreen(onBack = onBack, active = active, modifier = Modifier.fillMaxSize())
        Routes.MORE_CONNECTIONS -> ConnectionsScreen(onBack = onBack, onSwitchLogin = onSwitchLogin, active = active, modifier = Modifier.fillMaxSize())
        Routes.MORE_SERVER_INFORMATION -> ServerInformationScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_NETWORK -> OutboundProxyScreen(onBack = onBack, onOpenManagedProxy = onOpenManagedProxy, active = active, modifier = Modifier.fillMaxSize())
        Routes.MORE_APPEARANCE -> AppearanceScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_DIAGNOSTICS -> DiagnosticsScreen(onBack = onBack, active = active, modifier = Modifier.fillMaxSize())
        Routes.MORE_APPLICATIONS -> app.relaxkonos.mobile.ui.more.MobileApplicationsScreen(onBack = onBack,
            onOpenRoute = onOpenRoute, modifier = Modifier.fillMaxSize())
        Routes.MORE_HELP -> app.relaxkonos.mobile.ui.more.HelpScreen(onBack = onBack, onOpenRoute = onOpenRoute,
            onOpenServerCenter = { moreContainer.serverCenter.open() }, modifier = Modifier.fillMaxSize())
        Routes.MORE_SERVER_HTTPS -> CertificatesScreen(onBack = { onBack?.invoke() }, active = active, serverHttpsOnly = true, modifier = Modifier.fillMaxSize())
        Routes.MORE_HOST_SETTINGS -> app.relaxkonos.mobile.ui.more.HostSettingsScreen(onBack = onBack, onOpenServerHttps = { onOpenRoute(Routes.MORE_SERVER_HTTPS) }, modifier = Modifier.fillMaxSize(), active = active)
        Routes.MORE_ABOUT -> AboutScreen(onBack = onBack, modifier = Modifier.fillMaxSize(), active = active)
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
        *if (route == Routes.MORE_SERVER_HTTPS) arrayOf(WorkspaceDestination(Routes.MORE_SERVER_HTTPS, R.string.server_https_title)) else emptyArray(),
        WorkspaceDestination(Routes.MORE_SERVER_INFORMATION, R.string.more_server_information),
        WorkspaceDestination(Routes.MORE_NETWORK, R.string.workspace_proxy),
        WorkspaceDestination(Routes.MORE_DIAGNOSTICS, R.string.more_diagnostics),
        WorkspaceDestination(Routes.MORE_HELP, R.string.help_title),
        WorkspaceDestination(Routes.MORE_ABOUT, R.string.more_about),
    ).filter { it.id != Routes.MORE_NETWORK || app.relaxkonos.mobile.core.net.ServerCapabilities.DOCKER in app.relaxkonos.mobile.ui.common.appContainer().capabilities }
    val screenTitle = stringResource(when (route) {
        Routes.MORE_APPEARANCE -> R.string.appearance_title
        Routes.MORE_ACCOUNT_SECURITY -> R.string.account_security_title
        Routes.MORE_CONNECTIONS -> R.string.connections_title
        Routes.MORE_NETWORK -> R.string.proxy_title
        Routes.MORE_DIAGNOSTICS -> R.string.diagnostics_title
        Routes.MORE_ABOUT -> R.string.about_title
        else -> pages.first { it.id == route }.title
    })
    WorkspaceFrame(screenTitle, subtitle = stringResource(when (route) {
            Routes.MORE_HOST_SETTINGS, Routes.MORE_SERVER_HTTPS, Routes.MORE_SERVER_INFORMATION, Routes.MORE_NETWORK -> R.string.workspace_scope_host
            Routes.MORE_CONNECTIONS, Routes.MORE_DIAGNOSTICS -> R.string.workspace_scope_connection
            else -> R.string.workspace_scope_android
        }), pages = pages, selected = route, onSelect = onOpenRoute, onBack = onBack) {
        androidx.compose.runtime.key(appContainer().activeSession) { Box(Modifier.weight(1f)) {
            pages.forEach { page -> androidx.compose.runtime.key(page.id) {
                WorkspaceSection(route == page.id, Modifier.fillMaxSize()) {
                    MorePane(page.id, route == page.id, onOpenRoute, onBack.takeIf { route == page.id }, onSwitchLogin, onOpenManagedProxy)
                }
            } }
        } }
    }
}
