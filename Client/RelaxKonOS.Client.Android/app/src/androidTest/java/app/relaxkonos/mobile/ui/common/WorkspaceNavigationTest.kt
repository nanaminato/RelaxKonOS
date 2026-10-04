package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.StateRestorationTester
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class WorkspaceNavigationTest {
    @get:Rule val rule = createComposeRule()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun tab(id: Int) = rule.onNodeWithText(context.getString(id))

    @Test fun wideWorkspaceUsesSideNavigationAndKeepsDraftWhenResized() {
        val width = mutableStateOf(1100.dp)
        rule.setContent {
            MaterialTheme {
                Box(Modifier.requiredSize(width.value, 640.dp)) { Workspace() }
            }
        }
        val category = tab(R.string.workspace_overview).fetchSemanticsNode().boundsInRoot
        val draft = rule.onNodeWithTag("draft").fetchSemanticsNode().boundsInRoot
        assertTrue(category.right <= draft.left)
        rule.onNodeWithTag("draft").performTextInput("resize draft")
        tab(R.string.workspace_logs).performClick()
        tab(R.string.workspace_overview).performClick()
        rule.runOnIdle { width.value = 360.dp }
        rule.onNodeWithTag("draft").assertTextContains("resize draft")
        assertTrue(tab(R.string.workspace_overview).fetchSemanticsNode().boundsInRoot.bottom <=
            rule.onNodeWithTag("draft").fetchSemanticsNode().boundsInRoot.top)
    }

    @Test fun framePlacesBackAboveTabsAndKeepsTheActivePagesLeavePolicy() {
        val events = mutableListOf<String>()
        rule.setContent {
            MaterialTheme {
                var selected by remember { mutableStateOf("overview") }
                WorkspaceFrame("Workspace", screenTitle = "Page $selected", pages = listOf(
                    WorkspaceDestination("overview", R.string.workspace_overview),
                    WorkspaceDestination("logs", R.string.workspace_logs),
                ), selected = selected, onSelect = { selected = it }, onBack = { events += "exit" }) {
                    Box(Modifier.weight(1f)) {
                        listOf("overview", "logs").forEach { page -> key(page) {
                            WorkspaceSection(selected == page) {
                                ScreenHeader("Page $page", onBack = { events += page },
                                    trailing = { TextButton(onClick = {}) { Text("Action $page") } })
                                Text("Content $page")
                            }
                        } }
                    }
                }
            }
        }
        val back = rule.onNodeWithContentDescription(context.getString(R.string.common_back))
        val backBounds = back.fetchSemanticsNode().boundsInRoot
        assertTrue(tab(R.string.workspace_overview).fetchSemanticsNode().boundsInRoot.top >= backBounds.bottom)
        rule.onNodeWithText("Page overview").assertDoesNotExist()
        rule.onNodeWithText("Action overview").assertIsDisplayed()
        back.performClick()
        tab(R.string.workspace_logs).performClick()
        back.performClick()
        tab(R.string.workspace_overview).performClick()
        back.performClick()
        rule.runOnIdle { assertEquals(listOf("overview", "logs", "overview"), events) }
    }

    @Test fun allEightCategoriesAreReachableOnANarrowScreen() {
        rule.setContent {
            MaterialTheme {
                var selected by remember { mutableStateOf("overview") }
                Box(Modifier.requiredSize(360.dp, 640.dp)) {
                    WorkspaceColumn("Docker", null, listOf(
                        WorkspaceDestination("overview", R.string.workspace_overview),
                        WorkspaceDestination("containers", R.string.workspace_containers),
                        WorkspaceDestination("compose", R.string.workspace_compose),
                        WorkspaceDestination("images", R.string.workspace_images),
                        WorkspaceDestination("mirrors", R.string.workspace_mirrors),
                        WorkspaceDestination("proxy", R.string.workspace_proxy),
                        WorkspaceDestination("networks", R.string.workspace_networks),
                        WorkspaceDestination("volumes", R.string.workspace_volumes),
                    ), selected, { selected = it }) { Text("Selected: $selected") }
                }
            }
        }
        tab(R.string.workspace_volumes).performScrollTo().performClick()
        rule.onNodeWithText("Selected: volumes").assertIsDisplayed()
        tab(R.string.workspace_overview).performScrollTo().performClick()
        rule.onNodeWithText("Selected: overview").assertIsDisplayed()
    }

    @Test fun previouslyVisitedPanesDoNotConsumeTheActiveViewport() {
        val selected = mutableIntStateOf(0)
        rule.setContent {
            Column(Modifier.requiredSize(360.dp, 640.dp)) {
                repeat(3) { pane ->
                    WorkspaceSection(selected.intValue == pane, Modifier.weight(1f)) {
                        Box(Modifier.fillMaxSize().testTag("pane-$pane"))
                    }
                }
            }
        }
        val height = rule.onNodeWithTag("pane-0").fetchSemanticsNode().boundsInRoot.height
        rule.runOnIdle { selected.intValue = 1 }
        assertEquals(height, rule.onNodeWithTag("pane-1").fetchSemanticsNode().boundsInRoot.height, 1f)
        rule.runOnIdle { selected.intValue = 2 }
        assertEquals(height, rule.onNodeWithTag("pane-2").fetchSemanticsNode().boundsInRoot.height, 1f)
        rule.runOnIdle { selected.intValue = 0 }
        assertEquals(height, rule.onNodeWithTag("pane-0").fetchSemanticsNode().boundsInRoot.height, 1f)
    }

    @Composable
    private fun Workspace(onSubmit: () -> Unit = {}) {
        var selected by rememberSaveable { mutableStateOf("overview") }
        WorkspaceColumn("Test workspace", null, listOf(
            WorkspaceDestination("overview", R.string.workspace_overview),
            WorkspaceDestination("logs", R.string.workspace_logs),
        ), selected, { selected = it }) {
            WorkspaceSection(selected == "overview") {
                var draft by rememberSaveable { mutableStateOf("") }
                OutlinedTextField(draft, { draft = it }, Modifier.testTag("draft"))
                TextButton(onClick = onSubmit) { Text("Submit once") }
                repeat(40) { Text("Overview row $it") }
            }
            WorkspaceSection(selected == "logs") {
                repeat(40) { Text("Log row $it") }
            }
        }
    }

    @Test fun categorySwitchRetainsDraftAndNeverResubmits() {
        var submissions = 0
        rule.setContent { MaterialTheme { Workspace { submissions++ } } }
        rule.onNodeWithTag("draft").performTextInput("unsubmitted draft")
        rule.onNodeWithText("Submit once").performClick()
        tab(R.string.workspace_logs).performClick()
        rule.onNodeWithTag("draft").assertDoesNotExist()
        rule.onNodeWithText("Log row 0").assertIsDisplayed()
        tab(R.string.workspace_overview).performClick()
        rule.onNodeWithTag("draft").assertTextContains("unsubmitted draft")
        rule.runOnIdle { assertEquals(1, submissions) }
    }

    @Test fun navigationStaysReachableAtLargeFontAndRetainsEachScroll() {
        rule.setContent {
            MaterialTheme {
                val density = LocalDensity.current
                CompositionLocalProvider(LocalDensity provides Density(density.density, 2f)) {
                    Box(Modifier.requiredSize(360.dp, 640.dp)) { Workspace() }
                }
            }
        }
        rule.onNodeWithText("Overview row 39").performScrollTo().assertIsDisplayed()
        tab(R.string.workspace_logs).assertIsDisplayed().performClick()
        rule.onNodeWithText("Log row 0").assertIsDisplayed()
        rule.onNodeWithText("Log row 39").performScrollTo().assertIsDisplayed()
        tab(R.string.workspace_overview).performClick()
        rule.onNodeWithText("Overview row 39").assertIsDisplayed()
        tab(R.string.workspace_logs).performClick()
        rule.onNodeWithText("Log row 39").assertIsDisplayed()
    }

    @Test fun restoredCategoryAndDraftBelongToTheCurrentOwner() {
        val owner = mutableIntStateOf(1)
        val restoration = StateRestorationTester(rule)
        restoration.setContent { MaterialTheme { key(owner.intValue) { Workspace() } } }
        rule.onNodeWithTag("draft").performTextInput("owner-one")
        tab(R.string.workspace_logs).performClick()
        restoration.emulateSavedInstanceStateRestore()
        tab(R.string.workspace_logs).assertIsSelected()
        tab(R.string.workspace_overview).performClick()
        rule.onNodeWithTag("draft").assertTextContains("owner-one")
        rule.runOnIdle { owner.intValue = 2 }
        rule.onNodeWithTag("draft").assertTextEquals("")
    }
}
