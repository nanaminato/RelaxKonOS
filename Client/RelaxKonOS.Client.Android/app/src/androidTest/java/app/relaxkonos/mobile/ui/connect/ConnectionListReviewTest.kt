package app.relaxkonos.mobile.ui.connect

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.Density
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import app.relaxkonos.mobile.core.auth.CredentialStatus
import app.relaxkonos.mobile.security.model.SavedLogin
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ConnectionListReviewTest {
    @Test fun failedDeletionKeepsConfirmationAndCanRetryWithoutExposingException() {
        val login = SavedLogin("https://test.invalid", "alice", 1L, "Review record")
        var attempts = 0
        val container = (InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) {
            MaterialTheme { ConnectionListScreen(listOf(login), emptyList(), { CredentialStatus.None }, {}, {}, {}, {
                attempts++
                if (attempts == 1) throw IllegalStateException("private storage diagnostic")
            }, {}) }
        } }
        rule.onNodeWithContentDescription(text(R.string.connections_actions_title)).performClick()
        rule.onAllNodesWithText(text(R.string.common_delete)).filter(hasClickAction()).onLast().performClick()
        rule.onAllNodesWithText(text(R.string.common_delete)).filter(hasClickAction()).onLast().performClick()
        rule.onNodeWithText(text(R.string.connections_mutation_failed)).assertExists()
        rule.onNodeWithText("private storage diagnostic").assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsEnabled()
        rule.onAllNodesWithText(text(R.string.common_delete)).filter(hasClickAction()).onLast().performClick()
        rule.onNodeWithText(text(R.string.connections_delete_title)).assertDoesNotExist()
        rule.runOnIdle { assertEquals(2, attempts) }
    }
    @Test fun removedRecordDismissesConfirmationAndDoesNotReopenWhenRestored() {
        val login = SavedLogin("https://test.invalid", "alice", 1L, "Review record")
        val logins = mutableStateOf(listOf(login))
        var deleted = 0
        val container = (InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) {
            MaterialTheme { ConnectionListScreen(logins.value, emptyList(), { CredentialStatus.None }, {}, {}, {}, { deleted++ }, {}) }
        } }
        rule.onNodeWithContentDescription(text(R.string.connections_actions_title)).performClick()
        rule.onAllNodesWithText(text(R.string.common_delete)).filter(hasClickAction()).onLast().performClick()
        rule.onNodeWithText(text(R.string.connections_delete_title)).assertExists()
        rule.runOnIdle { logins.value = emptyList() }
        rule.onNodeWithText(text(R.string.connections_delete_title)).assertDoesNotExist()
        rule.runOnIdle { logins.value = listOf(login) }
        rule.onNodeWithText(text(R.string.connections_delete_title)).assertDoesNotExist()
        rule.runOnIdle { assertEquals(0, deleted) }
    }
    @Test fun absentCredentialDisablesForgetAndAChangedConfirmationCanStillBeCancelled() {
        val login = SavedLogin("https://test.invalid", "alice", 1L, "Review record")
        val status = mutableStateOf(CredentialStatus.None)
        var forgotten = 0
        val container = (InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) {
            MaterialTheme { ConnectionListScreen(listOf(login), emptyList(), { status.value }, {}, {}, { forgotten++ }, {}, {}) }
        } }
        rule.onNodeWithContentDescription(text(R.string.connections_actions_title)).performClick()
        rule.onAllNodesWithText(text(R.string.connections_forget_password)).filter(hasClickAction()).onLast().assertIsNotEnabled()
        rule.runOnIdle { status.value = CredentialStatus.Unavailable }
        rule.onAllNodesWithText(text(R.string.connections_forget_password)).filter(hasClickAction()).onLast().assertIsEnabled().performClick()
        rule.runOnIdle { status.value = CredentialStatus.None }
        rule.onAllNodesWithText(text(R.string.connections_forget_password)).filter(hasClickAction()).onLast().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals(0, forgotten) }
    }
    @Test fun manyRecordsCanScrollToAndSelectTheLastAccount() {
        val logins = (0 until 25).map { SavedLogin("https://test.invalid", "user-$it", 1L, "Record $it") }
        var selected: SavedLogin? = null
        val container = (InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) {
            MaterialTheme { ConnectionListScreen(logins, emptyList(), { CredentialStatus.None },
                { selected = it }, {}, {}, {}, {}) }
        } }
        rule.onNodeWithText("Record 24").performScrollTo().assertIsDisplayed().performClick()
        rule.runOnIdle { assertEquals(logins.last(), selected) }
    }
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun longNameActionsRemainReachableAndConfirmationAddressesOnlyTheSelectedAccount() {
        val first = SavedLogin("https://test.invalid", "alice", 1L, "Long server name ".repeat(50))
        val second = first.copy(identifier = "bob", displayName = "Other account")
        val forgotten = mutableListOf<SavedLogin>()
        val deleted = mutableListOf<SavedLogin>()
        var selected = 0
        val container = (InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RelaxKonApplication).container
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.5f), LocalAppContainer provides container) {
                MaterialTheme { ConnectionListScreen(listOf(first, second), emptyList(), { CredentialStatus.SavedByFingerprint },
                    { selected++ }, {}, { forgotten += it }, { deleted += it }, {}) }
            }
        }
        rule.onAllNodesWithContentDescription(text(R.string.connections_actions_title)).onFirst().performClick()
        rule.onAllNodesWithText(text(R.string.connections_forget_password)).filter(hasClickAction()).onLast()
            .performScrollTo().assertIsDisplayed().performClick()
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        rule.onNodeWithText(context.getString(R.string.connections_forget_message, first.serviceId, first.identifier)).assertExists()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertTrue(forgotten.isEmpty()); assertTrue(deleted.isEmpty()) }
        rule.onAllNodesWithContentDescription(text(R.string.connections_actions_title)).onFirst().performClick()
        rule.onAllNodesWithText(text(R.string.common_delete)).filter(hasClickAction()).onLast()
            .performScrollTo().assertIsDisplayed().performClick()
        rule.onNodeWithText(context.getString(R.string.connections_delete_message, first.serviceId, first.identifier)).assertExists()
        rule.onAllNodesWithText(text(R.string.common_delete)).filter(hasClickAction()).onLast().performClick()
        rule.runOnIdle { assertEquals(listOf(first), deleted); assertTrue(forgotten.isEmpty()); assertEquals(0, selected) }
    }
}
