package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.espresso.matcher.RootMatchers.isDialog
import androidx.test.espresso.matcher.ViewMatchers.isRoot
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ProxyGeoDataEditorTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun busyLocksPathAndFailurePreservesDraftWithExplicitDiscard() {
        val state = mutableStateOf(ProxyState())
        var left = false
        var submitted = ""
        rule.setContent { MaterialTheme { ProxyGeoDataEditor(state.value,
            { submitted = it; state.value = state.value.copy(busy = true) }, { left = true }) } }
        rule.onNodeWithText(text(R.string.mihomo_geodata_path)).performTextInput("/srv/review-geodata.dat")
        Espresso.onView(isRoot()).inRoot(isDialog()).perform(androidx.test.espresso.action.ViewActions.closeSoftKeyboard())
        rule.onNodeWithText(text(R.string.mihomo_apply_confirm)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        rule.onNodeWithText(text(R.string.mihomo_geodata_path)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.remote_path_browse)).assertIsNotEnabled()
        rule.runOnIdle { assertEquals("/srv/review-geodata.dat", submitted); state.value = state.value.copy(busy = false, uncertain = true) }
        rule.onNodeWithText(text(R.string.mihomo_uncertain)).performScrollTo().assertIsDisplayed()
        pressEditorBack()
        rule.onNodeWithText(text(R.string.mihomo_discard)).assertIsDisplayed()
        rule.onAllNodesWithText(text(R.string.common_cancel)).onLast().performClick()
        rule.onNodeWithText(text(R.string.mihomo_discard)).assertDoesNotExist()
        InstrumentationRegistry.getInstrumentation().uiAutomation.waitForIdle(500, 5_000)
        rule.onNodeWithText("/srv/review-geodata.dat").assertExists()
        rule.runOnIdle { assertFalse(left) }
        pressEditorBack()
        rule.onNodeWithText(text(R.string.common_close)).performClick()
        rule.runOnIdle { assertTrue(left) }
    }
    private fun pressEditorBack() {
        Espresso.onView(isRoot()).inRoot(isDialog()).perform(androidx.test.espresso.action.ViewActions.pressBack())
    }
}
