package app.relaxkonos.mobile.ui.more

import androidx.annotation.DrawableRes
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionGroup
import app.relaxkonos.mobile.ui.icons.DesktopIcon
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.nav.Routes
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * More.
 *
 * Settings groups only; the sign-out action is separated at the bottom so it cannot be hit while
 * reaching for a preference. The design's "list + detail" Expanded variant is served by the ordinary
 * pushed pages, which keeps one implementation of each settings page instead of two.
 *
 * Switch login and sign-out are separate actions below the settings. Sign-out retains its destructive
 * styling and confirmation; switching explicitly returns to the ordinary login picker.
 */
@Composable
fun MoreScreen(
    onOpenRoute: (String) -> Unit,
    onSignOut: () -> Unit,
    onSwitchLogin: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var confirmSignOut by remember { mutableStateOf(false) }

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(title = stringResource(R.string.more_title))

        SectionGroup {
            SettingsRow(icon = DesktopIcons.about, titleRes = R.string.mobile_apps_title,
                subtitleRes = R.string.mobile_apps_subtitle) { onOpenRoute(Routes.MORE_APPLICATIONS) }
            SettingsRow(icon = DesktopIcons.about, titleRes = R.string.help_title,
                subtitleRes = R.string.help_subtitle) { onOpenRoute(Routes.MORE_HELP) }

            SettingsRow(icon = DesktopIcons.host, titleRes = R.string.host_settings_title,
                subtitleRes = R.string.host_settings_subtitle) { onOpenRoute(Routes.MORE_HOST_SETTINGS) }

            if (ServerCapabilities.DOCKER in appContainer().capabilities) {
                SettingsRow(icon = DesktopIcons.connections, titleRes = R.string.proxy_title,
                    subtitleRes = R.string.proxy_subtitle) { onOpenRoute(Routes.MORE_NETWORK) }
            }
            SettingsRow(
                icon = DesktopIcons.credentials,
                titleRes = R.string.more_account_security,
                subtitleRes = R.string.more_account_security_subtitle,
            ) { onOpenRoute(Routes.MORE_ACCOUNT_SECURITY) }
            SettingsRow(
                icon = DesktopIcons.connections,
                titleRes = R.string.more_connections,
                subtitleRes = R.string.more_connections_subtitle,
            ) { onOpenRoute(Routes.MORE_CONNECTIONS) }
            SettingsRow(
                icon = DesktopIcons.host,
                titleRes = R.string.more_server_information,
                subtitleRes = R.string.more_server_information_subtitle,
            ) { onOpenRoute(Routes.MORE_SERVER_INFORMATION) }
            SettingsRow(
                icon = DesktopIcons.appearance,
                titleRes = R.string.more_appearance,
                subtitleRes = R.string.more_appearance_subtitle,
            ) { onOpenRoute(Routes.MORE_APPEARANCE) }
            SettingsRow(
                icon = DesktopIcons.diagnostics,
                titleRes = R.string.more_diagnostics,
                subtitleRes = R.string.more_diagnostics_subtitle,
            ) { onOpenRoute(Routes.MORE_DIAGNOSTICS) }
            SettingsRow(
                icon = DesktopIcons.about,
                titleRes = R.string.more_about,
                subtitleRes = R.string.more_about_subtitle,
            ) { onOpenRoute(Routes.MORE_ABOUT) }
        }

        OutlinedButton(onClick = onSwitchLogin, modifier = Modifier.fillMaxWidth()) {
            DesktopIcon(icon = DesktopIcons.connections, size = 18.dp)
            Spacer(Modifier.width(Spacing.sm))
            Text(stringResource(R.string.more_switch_login))
        }

        OutlinedButton(
            onClick = { confirmSignOut = true },
            modifier = Modifier.fillMaxWidth(),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
            border = BorderStroke(1.dp, MaterialTheme.colorScheme.error.copy(alpha = 0.5f)),
        ) {
            DesktopIcon(icon = DesktopIcons.signOut, size = 18.dp)
            Spacer(Modifier.width(Spacing.sm))
            Text(stringResource(R.string.more_sign_out))
        }
    }

    if (confirmSignOut) {
        ConfirmDangerousDialog(
            title = stringResource(R.string.more_sign_out),
            message = stringResource(R.string.more_sign_out_message),
            confirmLabel = stringResource(R.string.more_sign_out),
            onConfirm = {
                confirmSignOut = false
                onSignOut()
            },
            onDismiss = { confirmSignOut = false },
        )
    }
}

@Composable
private fun SettingsRow(@DrawableRes icon: Int, titleRes: Int, subtitleRes: Int, onOpen: () -> Unit) {
    ListRow(
        title = stringResource(titleRes),
        subtitle = stringResource(subtitleRes),
        leading = { IconBadge(icon = icon) },
        trailing = { DesktopIcon(icon = DesktopIcons.disclosure, size = 20.dp) },
        onClick = onOpen,
    )
}
