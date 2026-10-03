package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.servercenter.SshWorkspaceLayout
import app.relaxkonos.mobile.ui.servercenter.SshManagementTabs
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class SshWorkspaceLayoutTest {
    @get:Rule val rule = createComposeRule()

    @Test fun phoneManagementDestinationAndKeyboardNavigation() {
        val page = mutableIntStateOf(0)
        rule.setContent {
            MaterialTheme {
                Box(Modifier.requiredSize(360.dp, 640.dp)) {
                    SshWorkspaceLayout(page.intValue, { page.intValue = it }, page.intValue == 1) {
                        Text("page-${page.intValue}", it.testTag("workspace-content"))
                    }
                }
            }
        }
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        rule.onNodeWithText(context.getString(R.string.ssh_workspace_management)).performClick()
        rule.onNodeWithText("page-3").assertIsDisplayed()
        rule.onNodeWithText(context.getString(R.string.ssh_workspace_settings)).assertDoesNotExist()
        rule.onNodeWithText(context.getString(R.string.ssh_forward_title)).assertDoesNotExist()
        rule.runOnIdle { page.intValue = 1 }
        rule.onNodeWithTag("ssh-workspace-bar").assertDoesNotExist()
        rule.onNodeWithText("page-1").assertIsDisplayed()
    }

    @Test fun mediumWidthUsesRailBesideContentWhileTyping() = checkRail(700)
    @Test fun expandedWidthUsesRailBesideContentWhileTyping() = checkRail(1000)

    @Test fun managementTabsSwitchBetweenAllThreeTools() {
        val selected = mutableIntStateOf(0)
        rule.setContent {
            MaterialTheme { SshManagementTabs(selected.intValue, { selected.intValue = it }) }
        }
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val labels = listOf(R.string.ssh_workspace_system, R.string.ssh_workspace_settings, R.string.ssh_forward_title)
        labels.forEach { label ->
            rule.onNodeWithText(context.getString(label)).performClick().assertIsSelected()
            labels.filter { it != label }.forEach {
                rule.onNodeWithText(context.getString(it)).assertIsNotSelected()
            }
        }
    }

    private fun checkRail(width: Int) {
        rule.setContent {
            MaterialTheme {
                Box(Modifier.requiredSize(width.dp, 600.dp)) {
                    SshWorkspaceLayout(1, {}, true) { Text("terminal", it.testTag("workspace-content")) }
                }
            }
        }
        rule.onNodeWithTag("ssh-workspace-bar").assertDoesNotExist()
        val rail = rule.onNodeWithTag("ssh-workspace-rail").fetchSemanticsNode().boundsInRoot
        val content = rule.onNodeWithTag("workspace-content").fetchSemanticsNode().boundsInRoot
        assertTrue("The rail must leave a separate terminal viewport", content.left >= rail.right)
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        rule.onNodeWithContentDescription(context.getString(R.string.ssh_workspace_management)).assertExists()
    }
}
