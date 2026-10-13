package app.relaxkonos.mobile.ui.more

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.currentStateAsState
import app.relaxkonos.mobile.RelaxKonApplication
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
        val container = (rule.activity.application as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) { MaterialTheme {
            val lifecycle by LocalLifecycleOwner.current.lifecycle.currentStateAsState()
            SideEffect { foreground = lifecycle.isAtLeast(Lifecycle.State.RESUMED) }
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
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.waitUntil(5_000) { !foreground }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
            rule.waitUntil(5_000) { foreground }
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
}
