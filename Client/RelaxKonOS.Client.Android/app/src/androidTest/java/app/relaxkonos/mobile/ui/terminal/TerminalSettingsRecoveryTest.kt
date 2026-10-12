package app.relaxkonos.mobile.ui.terminal

import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class TerminalSettingsRecoveryTest {
    @get:Rule val rule = createComposeRule()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val p = TerminalPresentation()
    private val owner = SessionState.Active("server", "https://settings-review.invalid", "review", "work", emptySet(), "linux",
        ExecutionEligibility.Available, workspaceId = "11111111-1111-1111-1111-111111111111")
    private lateinit var controller: TerminalSettingsController
    private var cancelRead = false
    private var writes = 0
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun node(id: Int) = rule.onNodeWithText(context.getString(id))
    private fun show() {
        rule.runOnUiThread {
            controller = TerminalSettingsController(p, scope, { owner },
                { if (cancelRead) throw CancellationException("private settings detail") else ApiResult.Success(TerminalSettings.Default) },
                { _, _, _ -> writes++; throw CancellationException("private write detail") })
            controller.bindOwner(owner); controller.read(owner)
        }
        rule.setContent { MaterialTheme { TerminalAppearanceDialog(p, { controller.read(owner) }, { controller.save(owner, it) }) } }
        rule.waitUntil(5_000) { p.settingsVerified && !p.settingsBusy }
    }
    @After fun cleanup() { rule.runOnUiThread { scope.cancel() } }
    @Test fun cancelledReadDisablesSaveUntilActualRefresh() {
        show()
        rule.runOnIdle { cancelRead = true; controller.read(owner) }
        rule.waitUntil(5_000) { !p.settingsBusy && !p.settingsVerified }
        node(R.string.common_save).assertIsNotEnabled()
        node(R.string.terminal_settings_unavailable).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("private settings detail", substring = true).assertDoesNotExist()
        rule.runOnIdle { cancelRead = false }
        node(R.string.common_refresh).performScrollTo().performClick()
        rule.waitUntil(5_000) { p.settingsVerified && !p.settingsBusy }
        node(R.string.common_save).assertIsEnabled()
        rule.runOnIdle { assertEquals(0, writes) }
    }
    @Test fun cancelledSaveShowsUnknownAndRefreshDoesNotReplayWrite() {
        show(); node(R.string.common_save).performClick()
        rule.waitUntil(5_000) { !p.settingsBusy && !p.settingsVerified }
        node(R.string.common_save).assertIsNotEnabled()
        node(R.string.terminal_settings_unknown).performScrollTo().assertIsDisplayed()
        node(R.string.common_refresh).performScrollTo().performClick()
        rule.waitUntil(5_000) { p.settingsVerified && !p.settingsBusy }
        rule.runOnIdle { assertEquals(1, writes) }
        node(R.string.common_save).assertIsEnabled()
    }
}
