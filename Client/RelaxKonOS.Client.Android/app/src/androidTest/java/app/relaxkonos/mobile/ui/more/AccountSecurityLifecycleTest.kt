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
import androidx.lifecycle.LifecycleEventObserver
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.data.ReminderKind
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import app.relaxkonos.mobile.ui.common.WorkspaceSection
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class AccountSecurityLifecycleTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val active = mutableStateOf(true)
    private var mutations = 0
    private var sessionMatches = true
    @Volatile private var foreground = true
    private val record = DebugCredentialRecord("https://lifecycle-review.invalid", "review")
    private val actions = object : AccountSecurityActions {
        override val fingerprintEnabled = true
        override val capability = BiometricCapability.None
        override val connectionRecords = emptyList<VaultRecord>()
        override val elevationRecords = emptyList<VaultRecord>()
        override val debugRecord = record
        override val connectionMode: VaultUnlockMode? = null
        override val elevationMode: VaultUnlockMode? = null
        override val silenced = emptyList<ReminderKind>()
        override fun isCurrentSession(expected: SessionState.Active?) = sessionMatches && expected == null
        override suspend fun resolvePlatforms() {}
        override fun enableFingerprint() { mutations++ }
        override fun disableFingerprint() { mutations++ }
        override fun clearCredentials() { mutations++ }
        override fun delete(record: VaultRecord) { mutations++ }
        override fun deleteDebugCredential(record: DebugCredentialRecord) { mutations++ }
        override fun clearUsageMemory() { mutations++ }
        override fun restoreReminder(kind: ReminderKind) { mutations++ }
    }
    private fun text(id: Int) = rule.activity.getString(id)
    private fun show() {
        rule.activityRule.scenario.onActivity { activity ->
            activity.lifecycle.addObserver(LifecycleEventObserver { owner, _ ->
                foreground = owner.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
            })
        }
        val container = (rule.activity.application as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) { MaterialTheme {
            Column {
                WorkspaceSection(active.value) { AccountSecurityContent(actions, null, null, active.value) }
                WorkspaceSection(!active.value) { Text("Other settings") }
            }
        } } }
    }
    private fun openConfirmation(kind: String) {
        val action = when (kind) {
            "disable" -> R.string.account_security_fingerprint_subtitle
            "clear" -> R.string.account_security_clear_all
            else -> R.string.common_delete
        }
        rule.onNodeWithText(text(action)).performScrollTo().performClick()
        rule.onNode(isDialog()).assertExists()
        val message = when (kind) {
            "disable" -> text(R.string.account_security_disable_message)
            "clear" -> text(R.string.account_security_clear_all_message)
            else -> rule.activity.getString(R.string.account_security_delete_message, record.identifier, record.serviceId)
        }
        rule.onNodeWithText(message).assertIsDisplayed()
    }
    private fun verifyWithdrawal(kind: String, background: Boolean) {
        show(); openConfirmation(kind)
        if (background) {
            rule.mainClock.autoAdvance = false
            try {
                rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
                rule.waitUntil(5_000) { !foreground }
                rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
                rule.waitUntil(5_000) { foreground }
            } finally {
                rule.mainClock.autoAdvance = true
            }
        } else {
            rule.runOnIdle { active.value = false }
            rule.onNodeWithText("Other settings").assertIsDisplayed()
            rule.onNode(isDialog()).assertDoesNotExist()
            rule.runOnIdle { active.value = true }
        }
        rule.onNode(isDialog()).assertDoesNotExist()
        openConfirmation(kind)
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle {
            assertEquals(0, mutations)
            assertEquals(record, actions.debugRecord)
            assertTrue(actions.fingerprintEnabled)
        }
    }
    @Test fun hiddenSecurityPageWithdrawsCredentialDeletionConfirmation() = verifyWithdrawal("disable", false)
    @Test fun pausedSecurityPageWithdrawsCredentialDeletionConfirmation() = verifyWithdrawal("disable", true)
    @Test fun hiddenSecurityPageWithdrawsClearAllConfirmation() = verifyWithdrawal("clear", false)
    @Test fun pausedSecurityPageWithdrawsClearAllConfirmation() = verifyWithdrawal("clear", true)
    @Test fun hiddenSecurityPageWithdrawsSingleDeleteConfirmation() = verifyWithdrawal("delete", false)
    @Test fun pausedSecurityPageWithdrawsSingleDeleteConfirmation() = verifyWithdrawal("delete", true)
    private fun confirmCallback(kind: String): () -> Boolean {
        val label = when (kind) {
            "disable" -> R.string.account_security_disable_confirm
            "clear" -> R.string.account_security_clear_all
            else -> R.string.common_delete
        }
        return requireNotNull(rule.onNode(hasText(text(label)) and hasClickAction() and hasAnyAncestor(isDialog()))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action)
    }
    private fun verifyStaleCallback(kind: String, background: Boolean) {
        show(); openConfirmation(kind)
        val old = confirmCallback(kind)
        if (background) {
            rule.mainClock.autoAdvance = false
            try {
                rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
                rule.runOnUiThread { old(); assertEquals(0, mutations) }
                rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
            } finally { rule.mainClock.autoAdvance = true }
        } else {
            rule.runOnIdle { active.value = false }
            rule.onNodeWithText("Other settings").assertIsDisplayed()
            rule.runOnIdle { old(); assertEquals(0, mutations); active.value = true }
        }
        rule.onNode(isDialog()).assertDoesNotExist()
        openConfirmation(kind)
        rule.runOnIdle { old(); assertEquals(0, mutations) }
        rule.onNode(isDialog()).assertExists()
        val fresh = confirmCallback(kind)
        rule.runOnIdle { fresh(); fresh(); assertEquals(1, mutations) }
        rule.onNode(isDialog()).assertDoesNotExist()
    }
    @Test fun hiddenDisableRejectsOldCallbackAndFreshConfirmationIsConsumedOnce() = verifyStaleCallback("disable", false)
    @Test fun pausedDisableRejectsOldCallbackAndFreshConfirmationIsConsumedOnce() = verifyStaleCallback("disable", true)
    @Test fun hiddenClearAllRejectsOldCallbackAndFreshConfirmationIsConsumedOnce() = verifyStaleCallback("clear", false)
    @Test fun pausedClearAllRejectsOldCallbackAndFreshConfirmationIsConsumedOnce() = verifyStaleCallback("clear", true)
    @Test fun hiddenDeleteRejectsOldCallbackAndFreshConfirmationIsConsumedOnce() = verifyStaleCallback("delete", false)
    @Test fun pausedDeleteRejectsOldCallbackAndFreshConfirmationIsConsumedOnce() = verifyStaleCallback("delete", true)

    private fun verifyImmediateSessionChange(kind: String) {
        show(); openConfirmation(kind)
        val old = confirmCallback(kind)
        rule.mainClock.autoAdvance = false
        try {
            rule.runOnUiThread { sessionMatches = false; old(); assertEquals(0, mutations) }
        } finally { rule.mainClock.autoAdvance = true }
        rule.runOnIdle { sessionMatches = true }
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        openConfirmation(kind)
        rule.runOnIdle { old(); assertEquals(0, mutations) }
        val fresh = confirmCallback(kind)
        rule.runOnIdle { fresh(); fresh(); assertEquals(1, mutations) }
    }
    @Test fun sessionChangeBeforeRecompositionRejectsDisableCallback() = verifyImmediateSessionChange("disable")
    @Test fun sessionChangeBeforeRecompositionRejectsClearAllCallback() = verifyImmediateSessionChange("clear")
    @Test fun sessionChangeBeforeRecompositionRejectsSingleDeleteCallback() = verifyImmediateSessionChange("delete")

}
