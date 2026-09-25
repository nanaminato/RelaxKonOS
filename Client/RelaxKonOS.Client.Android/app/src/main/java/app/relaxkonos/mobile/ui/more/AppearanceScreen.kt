package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionGroup
import app.relaxkonos.mobile.ui.common.SectionLabel
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.theme.AppLanguage
import app.relaxkonos.mobile.ui.theme.ColorMode
import app.relaxkonos.mobile.ui.theme.Spacing
import app.relaxkonos.mobile.ui.theme.applyAppLanguage

/**
 * Appearance and language.
 *
 * Both settings are read back from `AppearanceState`, which persists before it reports the new value,
 * so what the screen shows is always what a restart would restore. The language change is applied
 * immediately through AppCompat, which recreates the activity — the session and the vaults live on the
 * application, so the user is not asked for a fingerprint again.
 *
 * Each group is captioned from outside its surface. The radio marks the selected option and is also the
 * row's trailing control, so the whole row is the target — a radio that only responds to a tap on a
 * 20dp circle is a needless miss.
 */
@Composable
fun AppearanceScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val appearance = appContainer().appearance

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.appearance_title),
            onBack = onBack,
        )

        Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
            SectionLabel(stringResource(R.string.appearance_color_mode))
            SectionGroup {
                ChoiceRow(R.string.appearance_color_follow_system, appearance.colorMode == ColorMode.FollowSystem) {
                    appearance.setColorMode(ColorMode.FollowSystem)
                }
                ChoiceRow(R.string.appearance_color_light, appearance.colorMode == ColorMode.Light) {
                    appearance.setColorMode(ColorMode.Light)
                }
                ChoiceRow(R.string.appearance_color_dark, appearance.colorMode == ColorMode.Dark) {
                    appearance.setColorMode(ColorMode.Dark)
                }
                ListRow(
                    title = stringResource(R.string.appearance_high_contrast),
                    trailing = {
                        Switch(
                            checked = appearance.highContrast,
                            onCheckedChange = { appearance.setHighContrast(it) },
                        )
                    },
                    onClick = { appearance.setHighContrast(!appearance.highContrast) },
                )
            }
        }

        Column(verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
            SectionLabel(stringResource(R.string.appearance_language))
            SectionGroup {
                ChoiceRow(R.string.appearance_language_follow_system, appearance.language == AppLanguage.FollowSystem) {
                    appearance.setLanguage(AppLanguage.FollowSystem)
                    applyAppLanguage(AppLanguage.FollowSystem)
                }
                ChoiceRow(R.string.appearance_language_english, appearance.language == AppLanguage.English) {
                    appearance.setLanguage(AppLanguage.English)
                    applyAppLanguage(AppLanguage.English)
                }
                ChoiceRow(R.string.appearance_language_chinese, appearance.language == AppLanguage.Chinese) {
                    appearance.setLanguage(AppLanguage.Chinese)
                    applyAppLanguage(AppLanguage.Chinese)
                }
                ChoiceRow(R.string.appearance_language_japanese, appearance.language == AppLanguage.Japanese) {
                    appearance.setLanguage(AppLanguage.Japanese)
                    applyAppLanguage(AppLanguage.Japanese)
                }
            }
            Text(
                stringResource(R.string.appearance_language_note),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(horizontal = Spacing.md),
            )
        }
    }
}

@Composable
private fun ChoiceRow(labelRes: Int, selected: Boolean, onSelect: () -> Unit) {
    ListRow(
        title = stringResource(labelRes),
        trailing = { RadioButton(selected = selected, onClick = onSelect) },
        onClick = onSelect,
    )
}
