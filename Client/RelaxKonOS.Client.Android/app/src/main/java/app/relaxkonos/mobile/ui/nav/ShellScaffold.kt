package app.relaxkonos.mobile.ui.nav

import android.app.Application
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.core.layout.layoutStateFor

/**
 * Owns shell navigation.
 *
 * Navigation lives in a ViewModel rather than in composition state so that a configuration change —
 * including the activity recreation a language switch causes — restores the user to the destination
 * they were on. The stacks are plain observable state, so the JVM tests can drive them without Compose.
 */
class ShellViewModel(application: Application) : AndroidViewModel(application) {
    val navigator = MobileNavigator(Routes.HOME)
}

/**
 * The authenticated shell.
 *
 * Three shapes, one source of truth for what exists: Compact uses a bottom bar, Medium a compact rail
 * and Expanded a full rail (`RelaxKonOS.Mobile.V1.Design.md` §3.2). The destination list is derived
 * from the server's advertised capabilities, so an entry that the server cannot serve is absent rather
 * than disabled.
 */
@Composable
fun ShellScaffold(
    container: AppContainer,
    navigator: MobileNavigator,
    session: SessionState.Active,
    onSignOut: () -> Unit,
) {
    BoxWithConstraints(modifier = Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background)) {
        val layoutState = layoutStateFor(maxWidth)
        val destinations = remember(session.capabilities) { TopDestination.visible(session.capabilities) }

        // The system back key unwinds the active destination's own stack first, and only then leaves
        // the destination (design §4.1).
        BackHandler(enabled = !navigator.isAtDestinationRoot) { navigator.pop() }

        fun select(destination: TopDestination) {
            if (navigator.currentDestination == destination.route) {
                navigator.popToDestinationRoot()
            } else {
                navigator.select(destination.route)
            }
        }

        when (layoutState) {
            LayoutState.Compact -> Scaffold(
                bottomBar = {
                    NavigationBar {
                        destinations.forEach { destination ->
                            NavigationBarItem(
                                selected = navigator.currentDestination == destination.route,
                                onClick = { select(destination) },
                                icon = { Icon(destination.icon, contentDescription = null) },
                                label = { Text(stringResource(destination.labelRes)) },
                            )
                        }
                    }
                },
            ) { padding ->
                MobileNavHost(
                    container = container,
                    navigator = navigator,
                    session = session,
                    layoutState = layoutState,
                    onSignOut = onSignOut,
                    modifier = Modifier.fillMaxSize().padding(padding),
                )
            }

            LayoutState.Medium, LayoutState.Expanded -> Row(Modifier.fillMaxSize()) {
                NavigationRail(Modifier.fillMaxHeight()) {
                    destinations.forEach { destination ->
                        NavigationRailItem(
                            selected = navigator.currentDestination == destination.route,
                            onClick = { select(destination) },
                            icon = { Icon(destination.icon, contentDescription = null) },
                            label = {
                                // A compact rail must stay narrow; the label is dropped below 840dp.
                                if (layoutState == LayoutState.Expanded) {
                                    Text(stringResource(destination.labelRes))
                                }
                            },
                        )
                    }
                }
                MobileNavHost(
                    container = container,
                    navigator = navigator,
                    session = session,
                    layoutState = layoutState,
                    onSignOut = onSignOut,
                    modifier = Modifier.fillMaxSize().padding(horizontal = 8.dp),
                )
            }
        }
    }
}
