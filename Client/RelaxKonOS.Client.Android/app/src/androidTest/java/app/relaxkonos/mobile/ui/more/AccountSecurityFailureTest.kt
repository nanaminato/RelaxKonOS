package app.relaxkonos.mobile.ui.more

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.data.ReminderKind
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class AccountSecurityFailureTest {
    @get:Rule val rule = createComposeRule()
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)
    private val original = DebugCredentialRecord("https://security-review.invalid", "review")
    private val replacement = DebugCredentialRecord("https://replacement.invalid", "other")
    private class Actions(val initial: DebugCredentialRecord) : AccountSecurityActions {
        override var fingerprintEnabled = true
        override val capability = BiometricCapability.None
        override val connectionRecords = emptyList<VaultRecord>()
        override val elevationRecords = emptyList<VaultRecord>()
        override var debugRecord: DebugCredentialRecord? = initial
        override val connectionMode: VaultUnlockMode? = null
        override val elevationMode: VaultUnlockMode? = null
        override val silenced = emptyList<ReminderKind>()
        var attempts = 0
        var memoryAttempts = 0
        val deleted = mutableListOf<DebugCredentialRecord>()
        override suspend fun resolvePlatforms() {}
        override fun enableFingerprint() { fingerprintEnabled = true }
        private fun attempt() {
            attempts++
            if (attempts == 1) throw IllegalStateException("private keystore detail")
        }
        override fun disableFingerprint() { fingerprintEnabled = false; attempt(); debugRecord = null }
        override fun clearCredentials() { attempt(); debugRecord = null }
        override fun delete(record: VaultRecord) { error("Unexpected vault target") }
        override fun deleteDebugCredential(record: DebugCredentialRecord) {
            deleted += record; attempt()
            if (debugRecord == record) debugRecord = null
        }
        override fun clearUsageMemory() {
            memoryAttempts++
            if (memoryAttempts > 1) throw IllegalStateException("private memory detail")
        }
        override fun restoreReminder(kind: ReminderKind) {}
    }
    private fun show(actions: Actions) {
        val container = (context.applicationContext as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) { MaterialTheme {
            AccountSecurityContent(actions, null, null, true)
        } } }
    }
    private fun submit(id: Int) = rule.onAllNodesWithText(text(id)).filter(hasClickAction()).onLast().performClick()
    private fun assertFailure(title: Int, actions: Actions) {
        rule.onNode(hasText(text(title)) and hasAnyAncestor(isDialog()) and !hasClickAction()).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.account_security_mutation_failed)).assertIsDisplayed()
        rule.onNodeWithText("private keystore detail").assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsEnabled()
        rule.runOnIdle { assertEquals(1, actions.attempts) }
    }
    @Test fun partialDisableFailureKeepsConfirmationAndCanFinishCleanup() {
        val actions = Actions(original); show(actions)
        rule.onNodeWithText(text(R.string.account_security_fingerprint_subtitle)).performScrollTo().performClick()
        submit(R.string.account_security_disable_confirm)
        assertFailure(R.string.account_security_disable_title, actions)
        rule.runOnIdle { assertFalse(actions.fingerprintEnabled); assertEquals(original, actions.debugRecord) }
        submit(R.string.account_security_disable_confirm)
        rule.onNodeWithText(text(R.string.account_security_disable_title)).assertDoesNotExist()
        rule.runOnIdle { assertEquals(2, actions.attempts); assertNull(actions.debugRecord) }
    }
    @Test fun clearAllFailureKeepsConfirmationAndAllowsRetry() {
        val actions = Actions(original); show(actions)
        rule.onNodeWithText(text(R.string.account_security_clear_all)).performScrollTo().performClick()
        submit(R.string.account_security_clear_all)
        assertFailure(R.string.account_security_clear_all_title, actions)
        submit(R.string.account_security_clear_all)
        rule.onNode(isDialog()).assertDoesNotExist()
        rule.runOnIdle { assertEquals(2, actions.attempts); assertNull(actions.debugRecord) }
    }
    @Test fun failedSingleDeleteRetriesOriginalIdentityAfterRecordReplacement() {
        val actions = Actions(original); show(actions)
        rule.onNodeWithText(text(R.string.common_delete)).performScrollTo().performClick()
        submit(R.string.common_delete)
        assertFailure(R.string.account_security_delete_title, actions)
        rule.runOnIdle { actions.debugRecord = replacement }
        submit(R.string.common_delete)
        rule.onNodeWithText(text(R.string.account_security_delete_title)).assertDoesNotExist()
        rule.runOnIdle {
            assertEquals(listOf(original, original), actions.deleted)
            assertEquals(replacement, actions.debugRecord)
        }
    }
    @Test fun failedMemoryClearRemovesPreviousSuccessNotice() {
        val actions = Actions(original); show(actions)
        rule.onNodeWithText(text(R.string.usage_memory_clear)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.usage_memory_cleared)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.usage_memory_clear)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.account_security_mutation_failed)).assertIsDisplayed()
        rule.onNodeWithText("private memory detail").assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_dismiss)).performClick()
        rule.onNodeWithText(text(R.string.usage_memory_cleared)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.usage_memory_clear)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.account_security_mutation_failed)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(3, actions.memoryAttempts) }
    }
}
