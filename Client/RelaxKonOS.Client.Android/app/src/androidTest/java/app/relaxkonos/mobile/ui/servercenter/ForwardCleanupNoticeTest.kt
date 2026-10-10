package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class ForwardCleanupNoticeTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun retryStaysAvailableWithoutSshFormAndOnlySuccessfulCleanupRemovesTheNotice() {
        val uncertain = mutableStateOf(true)
        var attempts = 0
        val title = text(R.string.ssh_forward_title) + " · " + text(R.string.ssh_forward_cleanup_pending)
        rule.setContent { MaterialTheme { Column {
            Text("Login destination")
            if (uncertain.value) ForwardCleanupNotice({ attempts++; if (attempts == 2) uncertain.value = false })
        } } }
        rule.onNodeWithText(title).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.ssh_forward_stop_all)).performClick()
        rule.onNodeWithText(title).assertIsDisplayed().performClick()
        rule.onNodeWithText(text(R.string.ssh_forward_cleanup_failed)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_close)).performClick()
        rule.onNodeWithText(title).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.ssh_forward_stop_all)).performClick()
        rule.onNodeWithText(title).assertDoesNotExist()
        rule.onNodeWithText("Login destination").assertIsDisplayed()
        rule.runOnIdle { assertEquals(2, attempts) }
    }
}
