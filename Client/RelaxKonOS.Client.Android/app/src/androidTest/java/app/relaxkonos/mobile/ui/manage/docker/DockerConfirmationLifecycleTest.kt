package app.relaxkonos.mobile.ui.manage.docker

import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.test.espresso.Espresso
import app.relaxkonos.mobile.R
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class DockerConfirmationLifecycleTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val active = mutableStateOf(true)
    private val requested = mutableStateOf(true)
    private var changes = 0
    @Volatile private var foreground = true
    private fun text(id: Int) = rule.activity.getString(id)
    private fun observe() {
        rule.activityRule.scenario.onActivity { activity ->
            activity.lifecycle.addObserver(LifecycleEventObserver { owner, _ ->
                foreground = owner.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
            })
        }
    }
    private fun pauseAndResume() {
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.waitUntil(5_000) { !foreground }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
            rule.waitUntil(5_000) { foreground }
        } finally { rule.mainClock.autoAdvance = true }
    }
    private fun showRemoval() {
        observe()
        rule.setContent { MaterialTheme {
            if (requested.value) DockerRemovalConfirmation("Delete review-stack?", active.value,
                onConfirm = { changes++; requested.value = false }, onDismiss = { requested.value = false })
        } }
        rule.onNodeWithText("Delete review-stack?").assertIsDisplayed()
    }
    @Test fun hiddenDeletionRequiresAnotherRequestBeforeApproval() {
        showRemoval()
        rule.runOnIdle { active.value = false }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { active.value = true; assertFalse(requested.value); assertEquals(0, changes) }
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { requested.value = true }
        rule.onNodeWithText(text(R.string.common_delete)).performClick()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertEquals(1, changes) }
    }
    @Test fun pausedDeletionIsWithdrawnWithoutCompositionFrames() {
        showRemoval(); pauseAndResume()
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertFalse(requested.value); assertEquals(0, changes); requested.value = true }
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertEquals(0, changes) }
    }
    @Test fun pausedDiscardConfirmationPreservesComposerDraft() {
        observe()
        rule.setContent { MaterialTheme {
            DockerComposer("services:\n  app: {}\n", null, false, { _, _ -> }, {},
                { changes++ }, { _, _ -> changes++ })
        } }
        rule.onNodeWithText(text(R.string.docker_stack_name)).performTextInput("review-draft")
        Espresso.closeSoftKeyboard()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertIsDisplayed()
        pauseAndResume()
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertDoesNotExist()
        rule.onNodeWithText("review-draft").assertExists()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.ui_discard_draft_title)).assertIsDisplayed()
        rule.onAllNodesWithText(text(R.string.common_cancel)).onLast().performClick()
        rule.runOnIdle { assertEquals(0, changes) }
    }
}
