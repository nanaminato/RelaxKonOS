package app.relaxkonos.mobile.ui.more

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import app.relaxkonos.mobile.ui.common.WorkspaceSection
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ConnectionsLifecycleTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val active = mutableStateOf(true)
    private var container: AppContainer? = null
    private var added = false
    private var switches = 0
    private val service = "https://connections-lifecycle.invalid"
    private val account = "review-01a134ee"
    private fun text(id: Int) = rule.activity.getString(id)
    private fun show() {
        val current = (rule.activity.application as RelaxKonApplication).container
        container = current
        assertFalse(current.profiles.all().any { it.serviceId == service && it.identifier == account })
        val latest = current.profiles.all().maxOfOrNull { it.lastUsedEpochMillis } ?: System.currentTimeMillis()
        current.profiles.upsert(SavedLogin(service, account, latest + 60_000, "Lifecycle test record"))
        added = true
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides current) { MaterialTheme {
            Column {
                WorkspaceSection(active.value) { ConnectionsScreen(onBack = null, onSwitchLogin = { switches++ }, active = active.value) }
                WorkspaceSection(!active.value) { Text("Other settings") }
            }
        } } }
    }
    @After fun cleanup() {
        if (added) container?.profiles?.remove(service, account)
    }
    private fun assertFixtureRemains() {
        assertTrue(container!!.profiles.all().any { it.serviceId == service && it.identifier == account })
        assertEquals(0, switches)
    }
    @Test fun hiddenConnectionsWithdrawsForgetConfirmation() {
        show()
        rule.onAllNodesWithText(text(R.string.connections_forget_password)).filter(hasClickAction()).onFirst().performScrollTo().performClick()
        rule.onNodeWithText(rule.activity.getString(R.string.connections_forget_message, service, account)).assertIsDisplayed()
        rule.runOnIdle { active.value = false }
        rule.onNodeWithText("Other settings").assertIsDisplayed()
        rule.onNodeWithText(text(R.string.connections_forget_title)).assertDoesNotExist()
        rule.runOnIdle { active.value = true }
        rule.onNodeWithText(text(R.string.connections_forget_title)).assertDoesNotExist()
        rule.runOnIdle { assertFixtureRemains() }
    }
    @Test fun pausedConnectionsWithdrawsDeleteConfirmation() {
        show()
        rule.onAllNodesWithText(text(R.string.common_delete)).filter(hasClickAction()).onFirst().performScrollTo().performClick()
        rule.onNodeWithText(rule.activity.getString(R.string.connections_delete_message, service, account)).assertIsDisplayed()
        rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
        rule.waitForIdle()
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        rule.onNodeWithText(text(R.string.connections_delete_title)).assertDoesNotExist()
        rule.runOnIdle { assertFixtureRemains() }
    }
}
