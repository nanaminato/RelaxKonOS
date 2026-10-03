package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.BuildConfig
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.theme.Spacing

/** Client version and third-party licences. */
@Composable
fun AboutScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    var showLicenses by rememberSaveable { mutableStateOf(false) }
    BackHandler(enabled = showLicenses) { showLicenses = false }
    if (showLicenses) {
        LicenseNoticesScreen(onBack = { showLicenses = false }, modifier = modifier)
        return
    }
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

        SectionCard(stringResource(R.string.about_licenses_title)) {
            Text(stringResource(R.string.about_licenses_body), style = MaterialTheme.typography.bodyMedium)
            TextButton(onClick = { showLicenses = true }) {
                Text(stringResource(R.string.about_licenses_view))
            }
        }
    }
}

@Composable
private fun LicenseNoticesScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val notices = remember(context) {
        context.assets.open("THIRD_PARTY_NOTICES.md").bufferedReader(Charsets.UTF_8).use { parseLicenseNotices(it.readText()) }
    }
    Column(
        modifier = modifier.fillMaxSize().padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(title = stringResource(R.string.about_licenses_title), onBack = onBack)
        SelectionContainer(modifier = Modifier.weight(1f).verticalScroll(rememberScrollState())) {
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                notices.forEach { notice ->
                    SectionCard(title = licenseDisplayText(notice.title)) {
                        notice.blocks.forEach { block ->
                            Text(
                                text = licenseDisplayText(block),
                                style = if (block.startsWith("### ")) MaterialTheme.typography.titleSmall
                                    else MaterialTheme.typography.bodyMedium,
                            )
                        }
                    }
                }
            }
        }
    }
}
