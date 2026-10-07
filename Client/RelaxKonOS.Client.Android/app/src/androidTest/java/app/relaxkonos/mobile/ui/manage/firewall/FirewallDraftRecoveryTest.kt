package app.relaxkonos.mobile.ui.manage.firewall

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.FirewallRule
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class FirewallDraftRecoveryTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun unverifiedFactsKeepRuleVisibleAndRefreshLocksWhileBusy() {
        val busy = mutableStateOf(false)
        var refreshed = 0
        var closed = false
        val draft = FirewallRule(0, "allow", "in", "tcp", "192.0.2.1", "any", "8443", "IPv4")
        rule.setContent { MaterialTheme {
            FirewallRetainedDraftDialog(draft, null, busy.value, null,
                { refreshed++; busy.value = true }, { closed = true })
        } }
        rule.onNodeWithText("192.0.2.1 → any:8443").assertIsDisplayed()
        rule.onNodeWithText(text(R.string.firewall_refresh_keep_draft)).performClick()
        rule.onNodeWithText(text(R.string.firewall_refresh_keep_draft)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsNotEnabled()
        Espresso.pressBack()
        rule.runOnIdle { assertEquals(1, refreshed); assertFalse(closed); busy.value = false }
        rule.onNodeWithText("192.0.2.1 → any:8443").assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertTrue(closed) }
    }
}
