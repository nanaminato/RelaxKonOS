package app.relaxkonos.mobile

import android.content.Intent
import android.os.Bundle
import androidx.activity.compose.setContent
import androidx.appcompat.app.AppCompatActivity
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.data.ForegroundAlertNotifier
import app.relaxkonos.mobile.ui.common.AppBackdrop
import app.relaxkonos.mobile.ui.common.ElevationDialog
import app.relaxkonos.mobile.ui.common.ActionFeedback
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import app.relaxkonos.mobile.ui.common.collectAsStateValue
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.connect.LoginScreen
import app.relaxkonos.mobile.ui.connect.LoginViewModel
import app.relaxkonos.mobile.ui.connect.OwnerDevicePairingScreen
import app.relaxkonos.mobile.ui.servercenter.ServerCenterScreen
import app.relaxkonos.mobile.ui.servercenter.SshWorkspaceScreen
import app.relaxkonos.mobile.ui.nav.Routes
import app.relaxkonos.mobile.ui.nav.ShellScaffold
import app.relaxkonos.mobile.ui.nav.ShellViewModel
import app.relaxkonos.mobile.ui.theme.RelaxKonOSTheme
import app.relaxkonos.mobile.ui.theme.Spacing
import app.relaxkonos.mobile.ui.theme.applyAppLanguage
import app.relaxkonos.mobile.ui.theme.applyAppNightMode
import kotlinx.coroutines.launch
import kotlinx.coroutines.CancellationException

/**
 * The single activity. It hosts Compose and nothing else.
 *
 * It extends `AppCompatActivity` for one reason: per-app language selection below API 33 is implemented
 * by AppCompat, and that requires an AppCompat host activity (`Shell.Design.md` §3.3,
 * `more/appearance`). It is also a `FragmentActivity`, which is what `BiometricPrompt` needs.
 */
class MainActivity : AppCompatActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        val container = (application as RelaxKonApplication).container
        // Locale and night mode are applied before super.onCreate: AppCompat resolves both while
        // attaching the base context, so applying them later would cost a second recreation.
        runCatching { applyAppLanguage(container.appearance.language) }
        runCatching { applyAppNightMode(container.appearance.colorMode) }
        super.onCreate(savedInstanceState)
        container.requestAlertOpen(intent?.getStringExtra(ForegroundAlertNotifier.EXTRA_OWNER_TOKEN))
        setContent {
            CompositionLocalProvider(LocalAppContainer provides container) {
                RelaxKonApp(container)
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        (application as RelaxKonApplication).container.requestAlertOpen(
            intent.getStringExtra(ForegroundAlertNotifier.EXTRA_OWNER_TOKEN))
    }

    override fun onStart() {
        super.onStart()
        (application as RelaxKonApplication).container.foregroundAlertNotifier.start()
    }

    override fun onStop() {
        (application as RelaxKonApplication).container.foregroundAlertNotifier.stop()
        super.onStop()
    }
}

/**
 * Root composable: sign-in until the session is active, the shell afterwards.
 *
 * The screen switch is driven by `AuthSession`'s state flow rather than by a callback from the login
 * screen, so a session lost to a rejected refresh lands on the same sign-in screen as an explicit
 * sign-out — with the stored credential still in the vault, ready for one fingerprint.
 *
 * The window backdrop is painted once, here, and both branches draw on top of it. Putting it in one
 * place is what keeps the sign-in screen and the shell from disagreeing about what "the background" is,
 * and it also means nothing has to know the palette to inherit it.
 */
@Composable
private fun RelaxKonApp(container: AppContainer) {
    val appearance = container.appearance
    RelaxKonOSTheme(colorMode = appearance.colorMode, highContrast = appearance.highContrast) {
        val sessionState = container.session.state.collectAsStateValue()
        val shell: ShellViewModel = viewModel()
        val login: LoginViewModel = viewModel()
        val scope = rememberCoroutineScope()
        var ownerDevicePairingOpen by rememberSaveable { mutableStateOf(false) }
        var changingSession by remember { mutableStateOf(false) }
        val activity = LocalContext.current as FragmentActivity

        fun endSession(switching: Boolean, target: SavedLogin? = null) {
            if (changingSession) return
            changingSession = true
            scope.launch {
                try {
                    container.session.logout()
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (_: Exception) {
                    // Local logout still completes when revoking the remote token fails.
                } finally {
                    changingSession = false
                    login.clearSessionInput()
                }
                shell.navigator.resetTo(Routes.HOME)
                ownerDevicePairingOpen = false
                if (switching) login.switchLogin(activity, target)
            }
        }

        LaunchedEffect((sessionState as? SessionState.Active)?.let { it.serviceId to it.effectiveBaseUrl }) {
            val active = sessionState as? SessionState.Active ?: return@LaunchedEffect
            container.hostOperatingSystems.resolve(listOf(active.serviceId), active)
        }

        // Sign-out and a rejected refresh both clear the navigation stacks;
        // the saved connection profiles are untouched (design §4.1, rule 3).
        LaunchedEffect(sessionState) {
            if (sessionState !is SessionState.Active) {
                shell.navigator.resetTo(Routes.HOME)
                ownerDevicePairingOpen = false
            }
        }

        LaunchedEffect(sessionState, container.pendingAlertOwnerToken) {
            val token = container.pendingAlertOwnerToken ?: return@LaunchedEffect
            if (sessionState is SessionState.Active) {
                if (ServerCapabilities.EVENT_ALERTS in sessionState.capabilities &&
                    token == container.foregroundAlertNotifier.ownerToken(sessionState)) {
                    shell.navigator.select(Routes.MANAGE)
                    shell.navigator.push(Routes.MANAGE_OPERATIONS)
                    container.prepareAlertScreen()
                }
                container.consumeAlertRequest()
            }
        }

        AppBackdrop {
            if (container.serverCenter.isOpen) {
                container.serverCenter.sshFilesHostId?.let { hostId ->
                    SshWorkspaceScreen(hostId = hostId, onClose = container.serverCenter::closeSshFiles)
                } ?: ServerCenterScreen(onClose = container.serverCenter::close)
            } else when (sessionState) {
                is SessionState.Active -> ShellScaffold(
                    container = container,
                    navigator = shell.navigator,
                    session = sessionState,
                    onSignOut = { endSession(switching = false) },
                    onSwitchLogin = { endSession(switching = true, target = it) },
                )

                else -> if (ownerDevicePairingOpen) {
                    OwnerDevicePairingScreen(onClose = { ownerDevicePairingOpen = false })
                } else {
                    LoginScreen(
                        viewModel = login,
                        onOpenServerCenter = container.serverCenter::open,
                        onOpenOwnerDevicePairing = { ownerDevicePairingOpen = true },
                    )
                }
            }

            container.pendingNotice?.let { notice ->
                ActionFeedback(
                    message = notice.text(),
                    onRetry = null,
                    onDismiss = container::dismissNotice,
                    // This banner floats over whatever screen is current, so it owns its own inset.
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .safeDrawingPadding()
                        .padding(Spacing.lg),
                )
            }
        }

        // Global overlays live outside the screen switch so navigating never dismisses a prompt the
        // user is still answering (design §3.4).
        ElevationDialog(container)
    }
}
