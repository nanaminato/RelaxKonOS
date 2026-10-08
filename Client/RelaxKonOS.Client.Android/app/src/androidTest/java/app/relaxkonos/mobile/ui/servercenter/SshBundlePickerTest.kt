package app.relaxkonos.mobile.ui.servercenter

import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.Density
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import app.relaxkonos.mobile.data.InMemoryUsageMemoryStorage
import app.relaxkonos.mobile.data.UsageMemoryStore
import app.relaxkonos.mobile.servercenter.SshFileEntry
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SshBundlePickerTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun idlePickerStopsAndClosesWhenAccountChanges() {
        val owner = SessionState.Active("test-server", "https://test", "alice", "main", emptySet(),
            "linux", ExecutionEligibility.Available, workspaceId = "test-workspace")
        val session = mutableStateOf<SessionState>(owner)
        val memory = UsageMemoryStore(InMemoryUsageMemoryStorage()).capture(owner) {
            session.value as? SessionState.Active
        }
        val events = mutableListOf<String>()
        rule.setContent {
            SshBundlePickerSessionGuard(session.value, memory, { events += "stop" }, { events += "dismiss" })
        }
        rule.runOnIdle { assertTrue(events.isEmpty()); session.value = owner.copy(userName = "bob") }
        rule.waitForIdle()
        rule.runOnIdle { assertEquals(listOf("stop", "dismiss"), events) }
    }

    @Test fun signedOutPickerStopsOnLoginWithoutRecapturingOwner() {
        val session = mutableStateOf<SessionState>(SessionState.SignedOut)
        val memory = UsageMemoryStore(InMemoryUsageMemoryStorage()).capture(null) {
            session.value as? SessionState.Active
        }
        var stopped = false
        var dismissed = false
        rule.setContent {
            SshBundlePickerSessionGuard(session.value, memory, { stopped = true }, { dismissed = true })
        }
        rule.runOnIdle {
            assertFalse(stopped); assertFalse(dismissed)
            session.value = SessionState.Active("test-server", "https://test", "alice", "main", emptySet(),
                "linux", ExecutionEligibility.Available, workspaceId = "test-workspace")
        }
        rule.waitForIdle()
        rule.runOnIdle { assertTrue(stopped); assertTrue(dismissed); assertFalse(memory.isCurrent) }
    }

    @Test fun longPathCanBeReadAndClosedWithoutLosingPackageSelection() {
        val path = "/srv/long-directory/".repeat(40)
        val bundle = SshFileEntry(path + "package.ZIP", "package.ZIP", false, false)
        val ignored = SshFileEntry(path + "ignored.txt", "ignored.txt", false, false)
        val link = SshFileEntry(path + "linked.zip", "linked.zip", false, true)
        var selected: SshFileEntry? = null
        var closed = false
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.5f)) {
                MaterialTheme { SshBundlePickerContent(
                    SshFilesUiState(path = path, connected = true, entries = listOf(bundle, ignored, link)),
                    { closed = true }, {}, {}, { selected = it }) }
            }
        }
        rule.onNodeWithText(path).assertIsDisplayed().performClick()
        rule.onNodeWithText(text(R.string.files_label_path)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_close)).assertIsDisplayed().performClick()
        rule.onNodeWithText("ignored.txt").assertDoesNotExist()
        rule.onNodeWithText("linked.zip").assertDoesNotExist()
        rule.onNodeWithText("package.ZIP").performScrollTo().assertIsDisplayed().performClick()
        rule.runOnIdle { assertEquals(bundle, selected); assertFalse(closed) }
    }

    @Test fun failedReadOffersRetryAndBusyReadCannotSelectStalePackage() {
        val bundle = SshFileEntry("/tmp/package.zip", "package.zip", false, false)
        val state = mutableStateOf(SshFilesUiState(path = "/tmp", connected = true,
            entries = listOf(bundle), problem = "connection-failed"))
        var retries = 0
        var opened = 0
        var closed = false
        rule.setContent { MaterialTheme {
            SshBundlePickerContent(state.value, { closed = true }, {}, { retries++ }, { opened++ })
        } }
        rule.onNodeWithText("package.zip").assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_retry)).performScrollTo().performClick()
        rule.runOnIdle { assertEquals(1, retries); state.value = state.value.copy(busy = true) }
        rule.onNodeWithText(text(R.string.common_retry)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_back)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertEquals(0, opened); assertTrue(closed) }
    }
}
