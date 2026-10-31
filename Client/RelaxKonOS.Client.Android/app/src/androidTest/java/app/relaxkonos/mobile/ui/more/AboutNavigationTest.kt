package app.relaxkonos.mobile.ui.more

import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.WorkspaceSection
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class AboutNavigationTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()

    @Test fun hiddenLicensePageDoesNotConsumeOtherCategoriesBack() {
        val active = mutableStateOf(true)
        var shellBacks = 0
        rule.setContent {
            MaterialTheme {
                BackHandler { shellBacks++ }
                Column {
                    WorkspaceSection(active.value) {
                        AboutScreen(onBack = null, active = active.value)
                    }
                    WorkspaceSection(!active.value) { Text("Other category") }
                }
            }
        }
        val context = rule.activity
        rule.onNodeWithText(context.getString(R.string.about_licenses_title)).performScrollTo().performClick()
        rule.onNodeWithContentDescription(context.getString(R.string.common_back)).assertIsDisplayed()
        rule.runOnIdle { active.value = false }
        rule.onNodeWithText("Other category").assertIsDisplayed()
        rule.runOnIdle { rule.activity.onBackPressedDispatcher.onBackPressed() }
        rule.runOnIdle { assertEquals(1, shellBacks); active.value = true }
        rule.onNodeWithContentDescription(context.getString(R.string.common_back)).assertIsDisplayed()
        rule.runOnIdle { rule.activity.onBackPressedDispatcher.onBackPressed() }
        rule.onNodeWithText(context.getString(R.string.about_licenses_title)).performScrollTo().assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, shellBacks) }
    }
}
