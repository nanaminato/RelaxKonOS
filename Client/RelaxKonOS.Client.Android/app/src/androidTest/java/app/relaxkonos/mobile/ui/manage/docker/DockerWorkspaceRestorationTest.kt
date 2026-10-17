package app.relaxkonos.mobile.ui.manage.docker

import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.StateRestorationTester
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import org.junit.Rule
import org.junit.Test

class DockerWorkspaceRestorationTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val initialSection = mutableStateOf("overview")
    private val initialStack = mutableStateOf<String?>(null)
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private fun tab(id: Int) = rule.onNode(hasText(text(id)) and isSelectable())
    private fun show(): StateRestorationTester {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication
        return StateRestorationTester(rule).also { tester ->
            tester.setContent { CompositionLocalProvider(LocalAppContainer provides app.container) {
                MaterialTheme { DockerWorkspace(initialSection.value, initialStack.value, {}, {}, {}) }
            } }
        }
    }
    @Test fun savedWorkspaceRestoresUserSelectedTabInsteadOfInitialOverview() {
        val tester = show()
        tab(R.string.workspace_containers).performClick().assertIsSelected()
        tester.emulateSavedInstanceStateRestore()
        tab(R.string.workspace_containers).assertIsSelected()
    }
    @Test fun newEntryRequestSelectsRequestedTabAndStack() {
        show()
        tab(R.string.workspace_containers).performClick().assertIsSelected()
        rule.runOnIdle { initialSection.value = "images" }
        tab(R.string.workspace_images).assertIsSelected()
        rule.runOnIdle { initialStack.value = "review-stack" }
        tab(R.string.workspace_compose).assertIsSelected()
    }
}
