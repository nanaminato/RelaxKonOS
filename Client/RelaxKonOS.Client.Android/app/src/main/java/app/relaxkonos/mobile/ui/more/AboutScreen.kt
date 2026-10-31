package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.FilledTonalButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.BuildConfig
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.theme.Spacing

/** Client version and third-party licences. */
@Composable
fun AboutScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
    active: Boolean = true,
) {
    var showLicenses by rememberSaveable { mutableStateOf(false) }
    BackHandler(enabled = active && showLicenses) { showLicenses = false }
    if (showLicenses) {
        LicenseNoticesScreen(onBack = { showLicenses = false }, modifier = modifier)
        return
    }
    Column(
        modifier = modifier.fillMaxSize().padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.about_title),
            onBack = onBack,
        )

        Column(
            modifier = Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(Spacing.md),
        ) {
            Spacer(Modifier.padding(top = Spacing.lg))
            Text(stringResource(R.string.app_name), style = MaterialTheme.typography.headlineMedium,
                textAlign = TextAlign.Center)
            Text(
                "${stringResource(R.string.about_client_version)} ${BuildConfig.VERSION_NAME}",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(stringResource(R.string.about_client_platform_value),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        FilledTonalButton(
            onClick = { showLicenses = true },
            modifier = Modifier.align(Alignment.CenterHorizontally).widthIn(max = 360.dp).fillMaxWidth(),
        ) {
            Text(stringResource(R.string.about_licenses_title), modifier = Modifier.padding(Spacing.sm))
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
        ScreenHeader(title = stringResource(R.string.about_licenses_title), onBack = onBack, compact = true)
        SelectionContainer(modifier = Modifier.weight(1f).verticalScroll(rememberScrollState())) {
            Column(verticalArrangement = Arrangement.spacedBy(Spacing.lg)) {
                notices.forEach { notice ->
                    Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
                        if (notice.title.isNotBlank()) Text(
                            text = licenseDisplayText(notice.title),
                            style = MaterialTheme.typography.titleSmall,
                            modifier = Modifier.semantics { heading() },
                        )
                        notice.blocks.forEach { block ->
                            Text(
                                text = licenseDisplayText(block),
                                style = if (block.startsWith("### ")) MaterialTheme.typography.titleSmall
                                    else MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                            )
                        }
                    }
                }
            }
        }
    }
}
