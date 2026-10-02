package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ProxyNodesPanelTest {
    @get:Rule val rule = createComposeRule()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val manual = ProxyGroup("Manual", "Selector", "Node A", listOf("Node A", "Node B", "Node C"))
    private val automatic = ProxyGroup("Automatic", "URLTest", "Auto A", listOf("Auto A", "Auto B"))

    @Test fun narrowPhoneUsesTwoColumnsAndNodeTapSelectsWithoutConfirmation() {
        val selections = mutableListOf<Pair<String, String>>()
        rule.setContent { MaterialTheme {
            Column(Modifier.width(320.dp).verticalScroll(rememberScrollState())) {
                ProxyNodesPanel(ProxyState(groups = ApiResult.Success(listOf(manual))), true, true, {},
                    { group, node -> selections += group.name to node }, {})
            }
        } }
        val a = rule.onNodeWithText("Node A").fetchSemanticsNode().boundsInRoot
        val b = rule.onNodeWithText("Node B").fetchSemanticsNode().boundsInRoot
        val c = rule.onNodeWithText("Node C").fetchSemanticsNode().boundsInRoot
        assertEquals(a.top, b.top, 1f); assertTrue(b.left > a.right); assertTrue(c.top >= a.bottom)
        rule.onNodeWithText("Node A").assertIsSelected()
        rule.onNodeWithText("Node B").performClick()
        rule.runOnIdle { assertEquals(listOf("Manual" to "Node B"), selections) }
    }

    @Test fun horizontalTabsShowOnlyCurrentGroupAndLightningTestsThatGroup() {
        val tests = mutableListOf<ProxyGroup>(); val selections = mutableListOf<String>()
        rule.setContent { MaterialTheme {
            Column(Modifier.width(360.dp).verticalScroll(rememberScrollState())) {
                ProxyNodesPanel(ProxyState(groups = ApiResult.Success(listOf(manual, automatic))), true, true, {},
                    { _, node -> selections += node }, { tests += it })
            }
        } }
        val manualTab = rule.onNodeWithText("Manual").fetchSemanticsNode().boundsInRoot
        val autoTab = rule.onNodeWithText("Automatic").fetchSemanticsNode().boundsInRoot
        assertEquals(manualTab.top, autoTab.top, 1f)
        rule.onNodeWithText("Automatic").performClick()
        rule.onNodeWithText("Node B").assertDoesNotExist()
        rule.onNodeWithText("Auto B").assertIsNotEnabled()
        rule.onNodeWithContentDescription(context.getString(R.string.mihomo_test_group_delay, "Automatic")).performClick()
        rule.runOnIdle { assertEquals(listOf(automatic), tests); assertTrue(selections.isEmpty()) }
    }

    @Test fun busyGroupShowsPerNodeResultsAndBlocksDuplicateRequests() {
        var writes = 0
        val state = ProxyState(busy = true, groups = ApiResult.Success(listOf(manual)), testingGroup = "Manual",
            testingProxies = setOf("Node C"), delays = mapOf("Node A" to ApiResult.Success(ProxyDelay("Node A", 42, false, "")),
                "Node B" to ApiResult.Transport(null)))
        rule.setContent { MaterialTheme {
            Column(Modifier.width(360.dp).verticalScroll(rememberScrollState())) {
                ProxyNodesPanel(state, true, false, {}, { _, _ -> writes++ }, { writes++ })
            }
        } }
        rule.onNodeWithText("Node B").assertIsNotEnabled()
        rule.onNodeWithContentDescription(context.getString(R.string.mihomo_test_group_delay, "Manual")).assertIsNotEnabled()
        rule.onNodeWithText(context.getString(R.string.mihomo_delay_result, 42)).assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.mihomo_node_test_failed)).assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.mihomo_node_testing)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, writes) }
    }
}
