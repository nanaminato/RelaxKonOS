package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

/** Server-reported support is informational; mobile entry points still use their own capability gates. */
@Composable
fun ServerInformationScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val session = appContainer().activeSession
    var showTechnicalDetails by rememberSaveable(session?.serviceId) { mutableStateOf(false) }
    val capabilities = session?.capabilities.orEmpty()
    val supportedFeatures = serverFeatureLabels.filter { (capability, _) -> capability in capabilities }
    val otherFeatureCount = capabilities.size - supportedFeatures.size

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(title = stringResource(R.string.more_server_information), onBack = onBack)

        SectionCard(stringResource(R.string.server_information_identity), leading = DesktopIcons.host) {
            if (session == null) {
                EmptyHint(stringResource(R.string.server_information_unavailable))
            } else {
                KeyValueRow(stringResource(R.string.home_label_server), session.serviceId)
                KeyValueRow(stringResource(R.string.home_label_platform), session.serverPlatform)
                KeyValueRow(stringResource(R.string.home_label_workspace), session.workspaceName)
            }
        }

        if (session != null) {
            SectionCard(
                title = stringResource(R.string.server_information_features),
                subtitle = stringResource(R.string.server_information_features_note),
                leading = DesktopIcons.capabilities,
            ) {
                if (capabilities.isEmpty()) {
                    EmptyHint(stringResource(R.string.server_information_features_empty))
                } else {
                    supportedFeatures.forEach { (_, labelRes) ->
                        Text(stringResource(labelRes), style = MaterialTheme.typography.bodyMedium)
                    }
                    if (otherFeatureCount > 0) {
                        Text(
                            stringResource(R.string.server_information_other_features, otherFeatureCount),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            SectionCard(
                title = stringResource(R.string.server_information_technical_details),
                trailing = {
                    TextButton(onClick = { showTechnicalDetails = !showTechnicalDetails }) {
                        Text(stringResource(
                            if (showTechnicalDetails) R.string.server_information_hide_details
                            else R.string.server_information_show_details,
                        ))
                    }
                },
            ) {
                if (showTechnicalDetails) {
                    if (capabilities.isEmpty()) {
                        EmptyHint(stringResource(R.string.server_information_features_empty))
                    } else {
                        SelectionContainer {
                            Text(
                                capabilities.sorted().joinToString("\n"),
                                style = MaterialTheme.typography.bodySmall,
                                fontFamily = FontFamily.Monospace,
                            )
                        }
                    }
                }
            }
        }
    }
}

/** Stable display order; unknown identifiers remain available only in technical details. */
private val serverFeatureLabels = listOf(
    ServerCapabilities.FILES to R.string.server_feature_files,
    ServerCapabilities.TERMINAL to R.string.server_feature_terminal,
    ServerCapabilities.METRICS to R.string.server_feature_metrics,
    ServerCapabilities.PROCESSES to R.string.server_feature_processes,
    ServerCapabilities.GUARDIAN to R.string.server_feature_guardian,
    ServerCapabilities.DOCKER to R.string.server_feature_docker,
    ServerCapabilities.APPLICATION_DEPLOYMENTS to R.string.server_feature_deployments,
    ServerCapabilities.WEB_SERVER to R.string.server_feature_web_server,
    ServerCapabilities.CERTIFICATES to R.string.server_feature_certificates,
    ServerCapabilities.GIT to R.string.server_feature_git,
    ServerCapabilities.EVENT_ALERTS to R.string.server_feature_alerts,
    ServerCapabilities.BACKUP_RECOVERY to R.string.server_feature_backup_recovery,
    ServerCapabilities.POSIX_PERMISSIONS to R.string.server_feature_posix_permissions,
    ServerCapabilities.FILE_SERVICES to R.string.server_feature_file_services,
    ServerCapabilities.FIREWALL to R.string.server_feature_firewall,
    ServerCapabilities.PROXY to R.string.server_feature_proxy,
    ServerCapabilities.TUNNELS to R.string.server_feature_tunnels,
)
