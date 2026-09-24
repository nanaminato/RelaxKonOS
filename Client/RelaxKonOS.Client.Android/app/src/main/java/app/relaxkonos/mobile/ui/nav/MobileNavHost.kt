package app.relaxkonos.mobile.ui.nav

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
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
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.files.FileDetailScreen
import app.relaxkonos.mobile.ui.files.FileMessageBanner
import app.relaxkonos.mobile.ui.files.FileOperationOverlays
import app.relaxkonos.mobile.ui.files.FileTransferCard
import app.relaxkonos.mobile.ui.files.FilesScreen
import app.relaxkonos.mobile.ui.files.FilesViewModel
import app.relaxkonos.mobile.ui.home.HomeScreen
import app.relaxkonos.mobile.ui.manage.ManageScreen
import app.relaxkonos.mobile.ui.manage.ManageViewModel
import app.relaxkonos.mobile.ui.manage.monitor.MonitorScreen
import app.relaxkonos.mobile.ui.manage.processes.ProcessesScreen
import app.relaxkonos.mobile.ui.more.AboutScreen
import app.relaxkonos.mobile.ui.more.AccountSecurityScreen
import app.relaxkonos.mobile.ui.more.AppearanceScreen
import app.relaxkonos.mobile.ui.more.ConnectionsScreen
import app.relaxkonos.mobile.ui.more.DiagnosticsScreen
import app.relaxkonos.mobile.ui.more.MoreScreen

/**
 * Maps the navigator's current route to a screen.
 *
 * The Expanded layout does not push sub-pages: `files/detail` becomes a second pane beside the list and
 * the manage and more destinations select a pane. Everything else is a pushed page with a back
 * affordance, which is the Compact and Medium shape (`RelaxKonOS.Mobile.V1.Design.md` §3.2, §4.1).
 */
@Composable
fun MobileNavHost(
    container: AppContainer,
    navigator: MobileNavigator,
    session: SessionState.Active,
    layoutState: LayoutState,
    onSignOut: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(modifier.fillMaxSize()) {
        when (navigator.route) {
            Routes.FILES, Routes.FILES_DETAIL -> FilesDestination(navigator, layoutState)
            Routes.MANAGE, Routes.MANAGE_MONITOR, Routes.MANAGE_PROCESSES -> ManageDestination(navigator, layoutState)
            Routes.MORE,
            Routes.MORE_ACCOUNT_SECURITY,
            Routes.MORE_CONNECTIONS,
            Routes.MORE_APPEARANCE,
            Routes.MORE_DIAGNOSTICS,
            Routes.MORE_ABOUT,
            -> MoreDestination(navigator, layoutState, onSignOut)

            else -> HomeScreen(
                session = session,
                layoutState = layoutState,
                onOpenFiles = { navigator.select(Routes.FILES) },
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
        }
        FileOperationOverlays(viewModel)
    }
}

@Composable
private fun ManageDestination(navigator: MobileNavigator, layoutState: LayoutState) {
    val viewModel: ManageViewModel = viewModel()

    if (layoutState == LayoutState.Expanded) {
        Row(Modifier.fillMaxSize()) {
            ManageScreen(
                onOpenMonitor = { viewModel.openPane(Routes.MANAGE_MONITOR) },
                onOpenProcesses = { viewModel.openPane(Routes.MANAGE_PROCESSES) },
                modifier = Modifier.weight(1f),
            )
            when (viewModel.expandedPane) {
                Routes.MANAGE_MONITOR -> MonitorScreen(onBack = null, modifier = Modifier.weight(1.2f))
                Routes.MANAGE_PROCESSES -> ProcessesScreen(onBack = null, modifier = Modifier.weight(1.2f))
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
        Routes.MANAGE_MONITOR -> MonitorScreen(onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize())
        Routes.MANAGE_PROCESSES -> ProcessesScreen(onBack = { navigator.pop() }, modifier = Modifier.fillMaxSize())
        else -> ManageScreen(
            onOpenMonitor = { navigator.push(Routes.MANAGE_MONITOR) },
            onOpenProcesses = { navigator.push(Routes.MANAGE_PROCESSES) },
            modifier = Modifier.fillMaxSize(),
        )
    }
}

@Composable
private fun MoreDestination(navigator: MobileNavigator, layoutState: LayoutState, onSignOut: () -> Unit) {
    var pane by remember { mutableStateOf<String?>(null) }

    if (layoutState == LayoutState.Expanded) {
        Row(Modifier.fillMaxSize()) {
            MoreScreen(
                onOpenRoute = { pane = it },
                onSignOut = onSignOut,
                modifier = Modifier.weight(1f),
            )
            Box(Modifier.weight(1.2f)) {
                if (pane == null) {
                    EmptyHint(stringResource(R.string.more_select_section), Modifier.padding(16.dp))
                } else {
                    MorePane(route = pane!!, onBack = null)
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
            modifier = Modifier.fillMaxSize(),
        )
    } else {
        MorePane(route = route, onBack = { navigator.pop() })
    }
}

/** One settings page, rendered either as a pushed page ([onBack] non-null) or as a pane. */
@Composable
private fun MorePane(route: String, onBack: (() -> Unit)?) {
    when (route) {
        Routes.MORE_ACCOUNT_SECURITY -> AccountSecurityScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_CONNECTIONS -> ConnectionsScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_APPEARANCE -> AppearanceScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_DIAGNOSTICS -> DiagnosticsScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        Routes.MORE_ABOUT -> AboutScreen(onBack = onBack, modifier = Modifier.fillMaxSize())
        else -> EmptyHint(stringResource(R.string.more_select_section))
    }
}
