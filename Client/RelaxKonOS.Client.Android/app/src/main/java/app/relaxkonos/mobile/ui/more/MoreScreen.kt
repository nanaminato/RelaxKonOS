package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.nav.Routes

/**
 * More.
 *
 * Settings groups only; the sign-out action is separated at the bottom so it cannot be hit while
 * reaching for a preference. The design's "list + detail" Expanded variant is served by the ordinary
 * pushed pages, which keeps one implementation of each settings page instead of two.
 */
@Composable
fun MoreScreen(
    onOpenRoute: (String) -> Unit,
    onSignOut: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var confirmSignOut by remember { mutableStateOf(false) }

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text(stringResource(R.string.more_title), style = MaterialTheme.typography.headlineSmall)

        SettingsRow(R.string.more_account_security, R.string.more_account_security_subtitle) {
            onOpenRoute(Routes.MORE_ACCOUNT_SECURITY)
        }
        SettingsRow(R.string.more_connections, R.string.more_connections_subtitle) {
            onOpenRoute(Routes.MORE_CONNECTIONS)
        }
        SettingsRow(R.string.more_appearance, R.string.more_appearance_subtitle) {
            onOpenRoute(Routes.MORE_APPEARANCE)
        }
        SettingsRow(R.string.more_diagnostics, R.string.more_diagnostics_subtitle) {
            onOpenRoute(Routes.MORE_DIAGNOSTICS)
        }
        SettingsRow(R.string.more_about, R.string.more_about_subtitle) {
            onOpenRoute(Routes.MORE_ABOUT)
        }

        OutlinedButton(onClick = { confirmSignOut = true }, modifier = Modifier.fillMaxWidth()) {
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
private fun SettingsRow(titleRes: Int, subtitleRes: Int, onOpen: () -> Unit) {
    Card(Modifier.fillMaxWidth().clickable { onOpen() }) {
        Row(modifier = Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(stringResource(titleRes), style = MaterialTheme.typography.titleMedium)
                Text(
                    stringResource(subtitleRes),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null)
        }
    }
}
