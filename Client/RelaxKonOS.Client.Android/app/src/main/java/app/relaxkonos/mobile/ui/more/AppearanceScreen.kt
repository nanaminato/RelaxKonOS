package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.theme.AppLanguage
import app.relaxkonos.mobile.ui.theme.ColorMode
import app.relaxkonos.mobile.ui.theme.applyAppLanguage

/**
 * Appearance and language.
 *
 * Both settings are read back from `AppearanceState`, which persists before it reports the new value,
 * so what the screen shows is always what a restart would restore. The language change is applied
 * immediately through AppCompat, which recreates the activity — the session and the vaults live on the
 * application, so the user is not asked for a fingerprint again.
 */
@Composable
fun AppearanceScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val appearance = appContainer().appearance

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (onBack != null) {
            TextButton(onClick = onBack) { Text(stringResource(R.string.common_back)) }
        }
        Text(stringResource(R.string.appearance_title), style = MaterialTheme.typography.headlineSmall)

        SectionCard(stringResource(R.string.appearance_color_mode)) {
            ChoiceRow(R.string.appearance_color_follow_system, appearance.colorMode == ColorMode.FollowSystem) {
                appearance.setColorMode(ColorMode.FollowSystem)
            }
            ChoiceRow(R.string.appearance_color_light, appearance.colorMode == ColorMode.Light) {
                appearance.setColorMode(ColorMode.Light)
            }
            ChoiceRow(R.string.appearance_color_dark, appearance.colorMode == ColorMode.Dark) {
                appearance.setColorMode(ColorMode.Dark)
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Switch(checked = appearance.highContrast, onCheckedChange = { appearance.setHighContrast(it) })
                Text(stringResource(R.string.appearance_high_contrast), modifier = Modifier.padding(start = 12.dp))
            }
        }

        SectionCard(stringResource(R.string.appearance_language)) {
            ChoiceRow(R.string.appearance_language_follow_system, appearance.language == AppLanguage.FollowSystem) {
                appearance.setLanguage(AppLanguage.FollowSystem)
                applyAppLanguage(AppLanguage.FollowSystem)
            }
            ChoiceRow(R.string.appearance_language_english, appearance.language == AppLanguage.English) {
                appearance.setLanguage(AppLanguage.English)
                applyAppLanguage(AppLanguage.English)
            }
            ChoiceRow(R.string.appearance_language_chinese, appearance.language == AppLanguage.SimplifiedChinese) {
                appearance.setLanguage(AppLanguage.SimplifiedChinese)
                applyAppLanguage(AppLanguage.SimplifiedChinese)
            }
            ChoiceRow(R.string.appearance_language_japanese, appearance.language == AppLanguage.Japanese) {
                appearance.setLanguage(AppLanguage.Japanese)
                applyAppLanguage(AppLanguage.Japanese)
            }
            Text(
                stringResource(R.string.appearance_language_note),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun ChoiceRow(labelRes: Int, selected: Boolean, onSelect: () -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth().selectable(selected = selected, onClick = onSelect),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RadioButton(selected = selected, onClick = onSelect)
        Text(stringResource(labelRes), modifier = Modifier.padding(start = 8.dp))
    }
}
