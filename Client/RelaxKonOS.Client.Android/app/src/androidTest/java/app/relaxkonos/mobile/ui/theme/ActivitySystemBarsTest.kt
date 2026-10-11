package app.relaxkonos.mobile.ui.theme

import android.content.res.Configuration
import androidx.activity.ComponentActivity
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.core.view.WindowCompat
import app.relaxkonos.mobile.ui.common.AppBackdrop
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class ActivitySystemBarsTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    @Test fun iconsFollowResolvedThemeAcrossExplicitModesAndHighContrast() {
        val mode = mutableStateOf(ColorMode.Light)
        val contrast = mutableStateOf(false)
        var contentColor: Color? = null
        var expectedContentColor: Color? = null
        rule.runOnUiThread {
            WindowCompat.getInsetsController(rule.activity.window, rule.activity.window.decorView).apply {
                isAppearanceLightStatusBars = false
                isAppearanceLightNavigationBars = false
            }
        }
        rule.setContent {
            RelaxKonOSTheme(mode.value, contrast.value) {
                ActivitySystemBars()
                val expected = MaterialTheme.colorScheme.onBackground
                AppBackdrop {
                    val actual = LocalContentColor.current
                    SideEffect { contentColor = actual; expectedContentColor = expected }
                    Text("Theme review")
                }
            }
        }
        fun check(light: Boolean) = rule.runOnIdle {
            val bars = WindowCompat.getInsetsController(rule.activity.window, rule.activity.window.decorView)
            assertEquals(light, bars.isAppearanceLightStatusBars)
            assertEquals(light, bars.isAppearanceLightNavigationBars)
            assertEquals(expectedContentColor, contentColor)
        }
        check(true)
        rule.runOnIdle { mode.value = ColorMode.Dark }
        check(false)
        rule.runOnIdle { contrast.value = true }
        check(false)
        rule.runOnIdle { mode.value = ColorMode.Light }
        check(true)
        rule.runOnIdle { mode.value = ColorMode.FollowSystem }
        check(rule.activity.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK != Configuration.UI_MODE_NIGHT_YES)
    }
}
