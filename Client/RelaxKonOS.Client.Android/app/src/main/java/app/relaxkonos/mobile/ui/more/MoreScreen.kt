package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ExitToApp
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.painter.Painter
import androidx.compose.ui.graphics.vector.rememberVectorPainter
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionGroup
import app.relaxkonos.mobile.ui.nav.Routes
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * More.
 *
 * Settings groups only; the sign-out action is separated at the bottom so it cannot be hit while
 * reaching for a preference. The design's "list + detail" Expanded variant is served by the ordinary
 * pushed pages, which keeps one implementation of each settings page instead of two.
 *
 * Sign-out is drawn as a destructive outlined button rather than as a list row: it is the one entry
 * here that ends the session, and it should not look like the five entries that only open a page.
 */
@Composable
fun MoreScreen(
    onOpenRoute: (String) -> Unit,
    onSignOut: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var confirmSignOut by remember { mutableStateOf(false) }

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(title = stringResource(R.string.more_title))

        SectionGroup {
            SettingsRow(
                icon = rememberVectorPainter(Icons.Filled.Lock),
                titleRes = R.string.more_account_security,
                subtitleRes = R.string.more_account_security_subtitle,
            ) { onOpenRoute(Routes.MORE_ACCOUNT_SECURITY) }
            SettingsRow(
                icon = painterResource(R.drawable.ic_link),
                titleRes = R.string.more_connections,
                subtitleRes = R.string.more_connections_subtitle,
            ) { onOpenRoute(Routes.MORE_CONNECTIONS) }
            SettingsRow(
                icon = rememberVectorPainter(Icons.Filled.Settings),
                titleRes = R.string.more_appearance,
                subtitleRes = R.string.more_appearance_subtitle,
            ) { onOpenRoute(Routes.MORE_APPEARANCE) }
            SettingsRow(
                icon = rememberVectorPainter(Icons.Filled.Warning),
                titleRes = R.string.more_diagnostics,
                subtitleRes = R.string.more_diagnostics_subtitle,
            ) { onOpenRoute(Routes.MORE_DIAGNOSTICS) }
            SettingsRow(
                icon = rememberVectorPainter(Icons.Filled.Info),
                titleRes = R.string.more_about,
                subtitleRes = R.string.more_about_subtitle,
            ) { onOpenRoute(Routes.MORE_ABOUT) }
        }

        OutlinedButton(
            onClick = { confirmSignOut = true },
            modifier = Modifier.fillMaxWidth(),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
            border = BorderStroke(1.dp, MaterialTheme.colorScheme.error.copy(alpha = 0.5f)),
        ) {
            Icon(Icons.AutoMirrored.Filled.ExitToApp, contentDescription = null, modifier = Modifier.size(18.dp))
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
private fun SettingsRow(icon: Painter, titleRes: Int, subtitleRes: Int, onOpen: () -> Unit) {
    ListRow(
        title = stringResource(titleRes),
        subtitle = stringResource(subtitleRes),
        leading = { IconBadge(icon = icon) },
        trailing = {
            Icon(
                Icons.AutoMirrored.Filled.KeyboardArrowRight,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        },
        onClick = onOpen,
    )
}
