package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.imePadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class ServerInstallVerificationTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun unknownResultKeepsRecoveryReachableAndLocksItWhileChecking() {
        val state = mutableStateOf(ServerInstallState(message = R.string.server_install_result_unknown,
            uncertain = true, needsVerification = true))
        val password = mutableStateOf("")
        var verified = ""
        var calls = 0
        rule.setContent { MaterialTheme {
            Column(Modifier.imePadding().verticalScroll(rememberScrollState())) {
                ServerInstallVerificationPanel(state.value, password.value, { password.value = it }) {
                    verified = password.value; calls++; state.value = state.value.copy(busy = true)
                }
            }
        } }
        rule.onNodeWithText(text(R.string.server_install_result_unknown)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.ssh_workspace_deploy_sudo_password)).performTextInput("test-only-secret")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.server_install_verify_original)).performScrollTo().performClick()
        rule.runOnIdle { assertEquals("test-only-secret", verified); assertEquals(1, calls); assertFalse(state.value.canSubmit) }
        rule.onNodeWithText(text(R.string.server_install_verify_original)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.ssh_workspace_deploy_sudo_password)).assertIsNotEnabled()
        rule.runOnIdle { state.value = state.value.copy(busy = false) }
        rule.onNodeWithText(text(R.string.server_install_result_unknown)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.server_install_verify_original)).assertIsEnabled()
    }

    @Test fun successfulReceiptWithUnverifiedHealthOffersVerificationWithoutNewSubmission() {
        var checks = 0
        val state = ServerInstallState(message = R.string.server_install_status_unverified,
            installed = true, needsVerification = true)
        rule.setContent { MaterialTheme {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                ServerInstallVerificationPanel(state, "", {}, { checks++ })
            }
        } }
        rule.onNodeWithText(text(R.string.server_install_status_unverified)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.server_install_verify_original)).performScrollTo().performClick()
        rule.runOnIdle { assertEquals(1, checks); assertFalse(state.canSubmit); assertFalse(state.uncertain) }
    }
}
