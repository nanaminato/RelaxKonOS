package app.relaxkonos.mobile.ui.more

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.CredentialStatus
import app.relaxkonos.mobile.security.model.SavedLogin
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ConnectionsFailureTest {
    @get:Rule val rule = createComposeRule()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private val login = SavedLogin("https://failure-review.invalid", "review", 1, "Failure review")

    private fun verifyFailureThenRetry(deleting: Boolean) {
        val records = mutableListOf(login)
        val targets = mutableListOf<SavedLogin>()
        val controller = object : ConnectionsActions {
            override fun isCurrentSession(expected: app.relaxkonos.mobile.core.auth.SessionState.Active?) = expected == null
            override val logins get() = records.toList()
            override suspend fun resolvePlatforms() = 0
            override fun status(login: SavedLogin) = CredentialStatus.None
            private fun mutate(target: SavedLogin) {
                targets += target
                if (targets.size == 1) throw IllegalStateException("private storage diagnostic")
                if (deleting) records.remove(target)
            }
            override fun delete(login: SavedLogin) = mutate(login)
            override fun forgetPassword(login: SavedLogin) = mutate(login)
        }
        val container = (context.applicationContext as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) { MaterialTheme {
            ConnectionsContent(controller, null, null, {}, {}, true)
        } } }
        val action = if (deleting) R.string.common_delete else R.string.connections_forget_password
        val title = if (deleting) R.string.connections_delete_title else R.string.connections_forget_title
        rule.onAllNodesWithText(text(action)).filter(hasClickAction()).onFirst().performScrollTo().performClick()
        rule.onNodeWithText(text(title)).assertIsDisplayed()
        rule.onAllNodesWithText(text(action)).filter(hasClickAction()).onLast().performClick()
        rule.onNodeWithText(text(title)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.connections_mutation_failed)).assertIsDisplayed()
        rule.onNodeWithText("private storage diagnostic").assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsEnabled()
        rule.runOnIdle { assertEquals(listOf(login), records); assertEquals(listOf(login), targets) }
        rule.onAllNodesWithText(text(action)).filter(hasClickAction()).onLast().performClick()
        rule.onNodeWithText(text(title)).assertDoesNotExist()
        rule.runOnIdle {
            assertEquals(listOf(login, login), targets)
            assertEquals(if (deleting) emptyList<SavedLogin>() else listOf(login), records)
        }
    }
    @Test fun failedDeleteKeepsConfirmationAndRetriesSameIdentity() = verifyFailureThenRetry(true)
    @Test fun failedForgetKeepsConfirmationAndRetriesSameIdentity() = verifyFailureThenRetry(false)
}
