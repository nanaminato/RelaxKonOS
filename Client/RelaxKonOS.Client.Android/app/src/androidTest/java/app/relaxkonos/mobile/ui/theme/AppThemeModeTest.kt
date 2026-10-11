package app.relaxkonos.mobile.ui.theme

import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.MainActivity
import app.relaxkonos.mobile.RelaxKonApplication
import org.junit.After
import org.junit.Rule
import org.junit.Test

/** Exercise the real root: a palette-only change leaves FollowSystem stuck in the startup override. */
class AppThemeModeTest {
    @get:Rule val rule = createAndroidComposeRule<MainActivity>()
    private val appearance get() = (InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication).container.appearance
    private var original: ColorMode? = null
    private fun select(mode: ColorMode, expectedNightMode: Int) {
        rule.runOnUiThread { appearance.setColorMode(mode) }
        rule.waitUntil(5_000) { AppCompatDelegate.getDefaultNightMode() == expectedNightMode }
        rule.waitForIdle()
    }
    @After fun restore() {
        original?.let { mode ->
            select(mode, when (mode) {
                ColorMode.Light -> AppCompatDelegate.MODE_NIGHT_NO
                ColorMode.Dark -> AppCompatDelegate.MODE_NIGHT_YES
                ColorMode.FollowSystem -> AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM
            })
        }
    }
    @Test fun explicitDarkThenFollowSystemUpdatesTheProductionWindowConfiguration() {
        original = appearance.colorMode
        select(ColorMode.Light, AppCompatDelegate.MODE_NIGHT_NO)
        select(ColorMode.Dark, AppCompatDelegate.MODE_NIGHT_YES)
        select(ColorMode.FollowSystem, AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM)
    }
}
