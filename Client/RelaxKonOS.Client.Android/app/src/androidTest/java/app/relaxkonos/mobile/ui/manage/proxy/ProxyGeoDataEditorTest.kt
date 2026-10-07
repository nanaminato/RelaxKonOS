package app.relaxkonos.mobile.ui.manage.proxy

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
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.mihomo_apply_confirm)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        rule.onNodeWithText(text(R.string.mihomo_geodata_path)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.remote_path_browse)).assertIsNotEnabled()
        rule.runOnIdle { assertEquals("/srv/review-geodata.dat", submitted); state.value = state.value.copy(busy = false, uncertain = true) }
        rule.onNodeWithText(text(R.string.mihomo_uncertain)).performScrollTo().assertIsDisplayed()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.mihomo_discard)).assertIsDisplayed()
        rule.onAllNodesWithText(text(R.string.common_cancel)).onLast().performClick()
        rule.onNodeWithText("/srv/review-geodata.dat").assertExists()
        rule.runOnIdle { assertFalse(left) }
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.common_close)).performClick()
        rule.runOnIdle { assertTrue(left) }
    }
}
