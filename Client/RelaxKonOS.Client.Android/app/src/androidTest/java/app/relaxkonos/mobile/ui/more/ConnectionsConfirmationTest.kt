package app.relaxkonos.mobile.ui.more

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import app.relaxkonos.mobile.ui.common.WorkspaceSection
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import app.relaxkonos.mobile.core.auth.CredentialStatus
import app.relaxkonos.mobile.security.model.SavedLogin

class ConnectionsConfirmationTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val active = mutableStateOf(true)
    private var mutations = 0
    private var sessionMatches = true
    private val login = SavedLogin("https://confirmation.invalid", "review", 1, "Review")
    private val actions = object : ConnectionsActions {
        override val logins get() = listOf(login)
        override fun isCurrentSession(expected: SessionState.Active?) = sessionMatches && expected == null
        override suspend fun resolvePlatforms() = 0
        override fun status(login: SavedLogin) = CredentialStatus.None
        override fun forgetPassword(login: SavedLogin) { mutations++ }
        override fun delete(login: SavedLogin) { mutations++ }
    }
    private fun text(id: Int) = rule.activity.getString(id)
    private fun show() {
        val container = (rule.activity.application as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) { MaterialTheme {
            Column {
                WorkspaceSection(active.value) { ConnectionsContent(actions, null, null, {}, {}, active.value) }
                WorkspaceSection(!active.value) { Text("Other settings") }
            }
        } } }
    }
    private fun open(deleting: Boolean) {
        val label = if (deleting) R.string.common_delete else R.string.connections_forget_password
        rule.onAllNodesWithText(text(label)).filter(hasClickAction()).onFirst().performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
    }
    private fun callback(deleting: Boolean): () -> Boolean {
        val label = if (deleting) R.string.common_delete else R.string.connections_forget_password
        return requireNotNull(rule.onNode(hasText(text(label)) and hasClickAction() and hasAnyAncestor(isDialog()))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action)
    }
    private fun verify(deleting: Boolean, boundary: String) {
        show(); open(deleting)
        val old = callback(deleting)
        when (boundary) {
            "hidden" -> {
                rule.runOnIdle { active.value = false }
                rule.onNodeWithText("Other settings").assertIsDisplayed()
                rule.runOnIdle { old(); assertEquals(0, mutations); active.value = true }
            }
            "paused" -> {
                rule.mainClock.autoAdvance = false
                try {
                    rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
                    rule.runOnUiThread { old(); assertEquals(0, mutations) }
                    rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
                } finally { rule.mainClock.autoAdvance = true }
            }
            else -> {
                rule.mainClock.autoAdvance = false
                try { rule.runOnUiThread { sessionMatches = false; old(); assertEquals(0, mutations) } }
                finally { rule.mainClock.autoAdvance = true }
                rule.runOnIdle { sessionMatches = true }
                rule.onNodeWithText(text(R.string.common_cancel)).performClick()
            }
        }
        rule.onNode(isDialog()).assertDoesNotExist()
        open(deleting)
        rule.runOnIdle { old(); assertEquals(0, mutations) }
        val fresh = callback(deleting)
        rule.runOnIdle { fresh(); fresh(); assertEquals(1, mutations) }
        rule.onNode(isDialog()).assertDoesNotExist()
    }
    @Test fun hiddenDeleteRejectsStaleConfirmation() = verify(true, "hidden")
    @Test fun hiddenForgetRejectsStaleConfirmation() = verify(false, "hidden")
    @Test fun pausedDeleteRejectsStaleConfirmation() = verify(true, "paused")
    @Test fun pausedForgetRejectsStaleConfirmation() = verify(false, "paused")
    @Test fun changedSessionRejectsDeleteBeforeRecomposition() = verify(true, "session")
    @Test fun changedSessionRejectsForgetBeforeRecomposition() = verify(false, "session")
}
