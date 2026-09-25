package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.BuildConfig
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.theme.Spacing

/** Version and server identification. Everything here is non-secret by construction. */
@Composable
fun AboutScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val container = appContainer()
    val session = container.activeSession

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.about_title),
            onBack = onBack,
        )

        SectionCard(stringResource(R.string.about_client_title)) {
            KeyValueRow(stringResource(R.string.about_client_version), BuildConfig.VERSION_NAME)
            KeyValueRow(stringResource(R.string.about_client_platform), stringResource(R.string.about_client_platform_value))
        }

        SectionCard(stringResource(R.string.about_server_title)) {
            if (session == null) {
                EmptyHint(stringResource(R.string.about_server_unavailable))
            } else {
                KeyValueRow(stringResource(R.string.home_label_server), session.serviceId)
                KeyValueRow(stringResource(R.string.home_label_platform), session.serverPlatform)
                KeyValueRow(stringResource(R.string.home_label_workspace), session.workspaceName)
                KeyValueRow(stringResource(R.string.about_server_capabilities), session.capabilities.size.toString())
            }
        }

        SectionCard(stringResource(R.string.about_licenses_title)) {
            Text(stringResource(R.string.about_licenses_body), style = MaterialTheme.typography.bodyMedium)
        }
    }
}
