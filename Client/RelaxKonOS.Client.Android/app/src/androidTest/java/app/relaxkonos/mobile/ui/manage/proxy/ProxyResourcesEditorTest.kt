package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ProxyResourcesEditorTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun preparationRequiresConfirmationAndDoesNotOfferAnArbitraryPath() {
        val state = mutableStateOf(ProxyState(resources = ApiResult.Success(ManagedRuntimeResources(
            "00112233-4455-6677-8899-aabbccddeeff", emptyList(), null))))
        var submitted = 0
        var closed = false
        rule.setContent { MaterialTheme { ProxyResourcesEditor(state.value,
            { submitted++; state.value = state.value.copy(busy = true) }, { closed = true }) } }
        rule.onAllNodesWithText(text(R.string.mihomo_geodata_prepare)).onLast().assertIsNotEnabled()
        rule.onNode(hasSetTextAction()).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.mihomo_apply_confirm)).performClick()
        rule.onAllNodesWithText(text(R.string.mihomo_geodata_prepare)).onLast().performClick()
        rule.runOnIdle { assertEquals(1, submitted); assertFalse(closed) }
        rule.onAllNodesWithText(text(R.string.mihomo_geodata_prepare)).onLast().assertIsNotEnabled()
        rule.runOnIdle { state.value = state.value.copy(busy = false, uncertain = true) }
        rule.onNodeWithText(text(R.string.mihomo_uncertain)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(1, submitted); state.value = state.value.copy(savedEpoch = state.value.savedEpoch + 1) }
        rule.runOnIdle { assertTrue(closed) }
    }
}
