package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SshFileNameDialogTest {
    @get:Rule val rule = createComposeRule()
    @Test fun busyAndUnknownPreserveDraftAndRecoveryAllowsCorrection() {
        val busy = mutableStateOf(true)
        val unknown = mutableStateOf(false)
        val value = mutableStateOf("review")
        var closed = 0
        var submitted = 0
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        rule.setContent { MaterialTheme {
            NameDialog("Review name", value.value, { value.value = it }, { submitted++ }, { closed++ }, busy.value, unknown.value)
        } }
        val save = rule.onNodeWithText(context.getString(R.string.common_save))
        save.assertIsNotEnabled()
        rule.onNodeWithText(context.getString(R.string.common_cancel)).assertIsNotEnabled()
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(0, closed); busy.value = false; unknown.value = true }
        save.assertIsNotEnabled()
        rule.onNodeWithText("review").assertExists()
        rule.onNodeWithText(context.getString(R.string.common_cancel)).assertIsEnabled()
        rule.runOnIdle { unknown.value = false }
        rule.onNode(hasSetTextAction()).performTextReplacement("../invalid")
        save.assertIsNotEnabled()
        rule.onNode(hasSetTextAction()).performTextReplacement("corrected")
        Espresso.closeSoftKeyboard()
        save.assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals("corrected", value.value); assertEquals(1, submitted); assertEquals(0, closed) }
    }
}
