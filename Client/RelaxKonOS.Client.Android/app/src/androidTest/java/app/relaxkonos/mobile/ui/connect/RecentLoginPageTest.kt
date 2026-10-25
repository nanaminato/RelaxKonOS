package app.relaxkonos.mobile.ui.connect

import android.graphics.Bitmap
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.test.core.app.ActivityScenario
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.MainActivity
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.security.model.SavedLogin
import java.io.File
import org.junit.Rule
import org.junit.Test

/** Exercise the production login entry without sending credentials or touching a remote server. */
class RecentLoginPageTest {
    @get:Rule val rule = createEmptyComposeRule()

    @Test fun recentServerSummaryKeepsManualPasswordAndConnectionSettingsReachable() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        val app = context.applicationContext as RelaxKonApplication
        val login = SavedLogin("https://desktop-preview.invalid", "desktop-review", Long.MAX_VALUE,
            displayName = "Home server")
        app.container.profiles.upsert(login)
        try {
            ActivityScenario.launch(MainActivity::class.java).use {
                rule.onNodeWithText("Home server").assertIsDisplayed()
                rule.onNodeWithText(context.getString(R.string.login_server_address)).assertDoesNotExist()
                rule.onNodeWithText(context.getString(R.string.login_password), useUnmergedTree = true).assertExists()
                instrumentation.uiAutomation.waitForIdle(300, 5_000)
                File(context.getExternalFilesDir(null), "recent-server-login.png").outputStream().use { out ->
                    instrumentation.uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, out)
                }
                rule.onNodeWithText(context.getString(R.string.desktop_connection_settings)).performScrollTo().performClick()
                rule.onNodeWithText(context.getString(R.string.login_server_address)).performScrollTo().assertIsDisplayed()
                rule.onNodeWithText(context.getString(R.string.desktop_hide_settings)).performScrollTo().performClick()
                rule.onNodeWithText("Home server").performScrollTo().assertIsDisplayed()
            }
        } finally {
            app.container.profiles.remove(login.serviceId, login.identifier)
        }
    }
}
