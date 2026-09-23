package app.relaxkonos.mobile.ui.nav

import android.app.Application
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material3.HorizontalDivider
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.layout.LayoutState
import app.relaxkonos.mobile.core.layout.layoutStateFor
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

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
 *
 * The shell paints nothing itself — its scaffolds are transparent so the window backdrop from
 * `MainActivity` shows through, which is what lets the sign-in screen and the shell share one
 * background. The navigation surfaces are opaque on purpose: a bar the user drags over must not make
 * the content behind it legible.
 */
@Composable
fun ShellScaffold(
    container: AppContainer,
    navigator: MobileNavigator,
    session: SessionState.Active,
    onSignOut: () -> Unit,
) {
    BoxWithConstraints(modifier = Modifier.fillMaxSize()) {
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
                containerColor = Color.Transparent,
                bottomBar = {
                    Column {
                        HorizontalDivider(
                            thickness = 1.dp,
                            color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f),
                        )
                        NavigationBar(
                            containerColor = MaterialTheme.colorScheme.surface,
                            tonalElevation = 0.dp,
                        ) {
                            destinations.forEach { destination ->
                                NavigationBarItem(
                                    selected = navigator.currentDestination == destination.route,
                                    onClick = { select(destination) },
                                    icon = {
                                        // Drawn with `Image`, not `Icon`: the desktop artwork is
                                        // full colour, and a Material tint would flatten it.
                                        DesktopIcon(icon = destination.iconRes, size = 26.dp)
                                    },
                                    label = { Text(stringResource(destination.labelRes)) },
                                )
                            }
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

            LayoutState.Medium, LayoutState.Expanded -> Row(Modifier.fillMaxSize().safeDrawingPadding()) {
                NavigationRail(
                    modifier = Modifier.fillMaxHeight(),
                    containerColor = MaterialTheme.colorScheme.surface,
                    // The surrounding Row already applied the window insets.
                    windowInsets = WindowInsets(0),
                    header = { RailHeader() },
                ) {
                    destinations.forEach { destination ->
                        NavigationRailItem(
                            selected = navigator.currentDestination == destination.route,
                            onClick = { select(destination) },
                            icon = { DesktopIcon(icon = destination.iconRes, size = 26.dp) },
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
                    // The rail is opaque, so the content needs no border of its own — only a gap.
                    modifier = Modifier.fillMaxSize().padding(horizontal = Spacing.xs),
                )
            }
        }
    }
}

/** Anchors the rail: without a mark at the top it reads as a row of loose icons. */
@Composable
private fun RailHeader() {
    DesktopIcon(
        icon = DesktopIcons.brand,
        size = 36.dp,
        modifier = Modifier.padding(bottom = Spacing.md),
    )
}
