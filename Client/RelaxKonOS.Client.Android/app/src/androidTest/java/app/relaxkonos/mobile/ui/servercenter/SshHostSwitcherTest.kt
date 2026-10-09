package app.relaxkonos.mobile.ui.servercenter

import android.graphics.Rect
import android.os.SystemClock
import android.view.InputDevice
import android.view.MotionEvent
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.ServerHostTarget
import app.relaxkonos.mobile.servercenter.ServerCenterHostKeyObservation
import app.relaxkonos.mobile.servercenter.ServerCenterSshVerification
import app.relaxkonos.mobile.servercenter.ServerHostKeyRecord
import app.relaxkonos.mobile.servercenter.ServerHostTrustRules
import app.relaxkonos.mobile.servercenter.SshFailureReason
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SshHostSwitcherTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun firstKeyReviewCanCancelBackToSwitcherWithoutTrusting() = keyReviewContract(false)
    @Test fun changedKeyShowsBothFingerprintsAndRequiresExplicitConfirmation() = keyReviewContract(true)

    private fun keyReviewContract(changed: Boolean) {
        val observation = ServerCenterHostKeyObservation("review.invalid", 22, "ssh-ed25519", byteArrayOf(9, 8, 7))
        val previous = ServerHostKeyRecord("review.invalid", 22, "ssh-ed25519", "AQID",
            ServerHostTrustRules.fingerprint(byteArrayOf(1, 2, 3)), 1_700_000_000_000)
        val result = if (changed) ServerCenterSshVerification.KeyChanged(observation, previous)
            else ServerCenterSshVerification.NeedsTrust(observation)
        val state = mutableStateOf(ServerCenterUiState(hosts = emptyList(), verification = result))
        var confirmations = 0
        var dismissals = 0
        var switcherClosed = false
        rule.setContent { MaterialTheme {
            SshHostSwitcherContent("current", state.value, true, {}, { switcherClosed = true },
                { confirmations++; state.value = state.value.copy(isVerifying = true) },
                { dismissals++; state.value = state.value.copy(verification = null) })
        } }
        rule.onNodeWithText(observation.groupedFingerprint, substring = true).assertIsDisplayed()
        rule.onNodeWithText("review.invalid").performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("22").performScrollTo().assertIsDisplayed()
        if (changed) {
            rule.onNodeWithText(ServerHostTrustRules.groupedFingerprint(previous.fingerprint), substring = true).assertIsDisplayed()
            rule.onNodeWithText(text(R.string.server_center_host_key_replace_confirm)).performClick()
            rule.onNodeWithText(text(R.string.common_cancel)).assertIsNotEnabled()
            Espresso.pressBack()
            rule.runOnIdle { assertEquals(1, confirmations); assertEquals(0, dismissals); assertFalse(switcherClosed); state.value = state.value.copy(isVerifying = false) }
        }
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText(text(R.string.server_center_switch_host_title)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(if (changed) 1 else 0, confirmations); assertEquals(1, dismissals); assertFalse(switcherClosed) }
    }

    @Test fun missingActivityDisablesTrustButAllowsReturningToHostList() {
        val state = mutableStateOf(ServerCenterUiState(hosts = emptyList(), verification =
            ServerCenterSshVerification.NeedsTrust(ServerCenterHostKeyObservation("review.invalid", 22, "ssh-ed25519", byteArrayOf(1)))))
        var confirmations = 0
        rule.setContent { MaterialTheme {
            SshHostSwitcherContent("current", state.value, false, {}, {}, { confirmations++ },
                { state.value = state.value.copy(verification = null) })
        } }
        rule.onNodeWithText(text(R.string.server_center_trust_and_verify)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsEnabled().performClick()
        rule.onNodeWithText(text(R.string.server_center_switch_host_title)).assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, confirmations) }
    }

    @Test fun largeFontLongListCanSelectLastHostAndStillCancel() {
        val hosts = (1..25).map { index ->
            ServerHostTarget("host-$index", "Host $index " + "Long name ".repeat(8), "review-$index.invalid", 22, "review", null, null, null, 0, 0)
        }
        var selected: String? = null
        var closed = false
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.5f)) { MaterialTheme {
                SshHostSwitcherContent("host-1", ServerCenterUiState(hosts), true, { selected = it }, { closed = true }, {}, {})
            } }
        }
        rule.onNodeWithText(hosts.last().displayName).performScrollTo().performClick()
        rule.runOnIdle { assertEquals("host-25", selected); assertFalse(closed) }
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsDisplayed().performClick()
        rule.runOnIdle { assertTrue(closed) }
    }

    @Test fun busySwitcherBlocksOutsideTouchAndRestoresDismissalAfterFailure() {
        val state = mutableStateOf(ServerCenterUiState(hosts = emptyList(), isVerifying = true))
        val dismissed = java.util.concurrent.atomic.AtomicInteger()
        rule.setContent { MaterialTheme {
            Text("Parent page")
            SshHostSwitcherContent("current", state.value, true, {}, { dismissed.incrementAndGet() }, {}, {})
        } }
        fun touchOutside() {
            val instrumentation = InstrumentationRegistry.getInstrumentation()
            instrumentation.waitForIdleSync()
            val bounds = Rect()
            Espresso.onView(androidx.test.espresso.matcher.ViewMatchers.isRoot())
                .inRoot(androidx.test.espresso.matcher.RootMatchers.isDialog())
                .check { view, failure ->
                    if (failure != null) throw failure
                    val location = IntArray(2)
                    view.getLocationOnScreen(location)
                    bounds.set(location[0], location[1], location[0] + view.width, location[1] + view.height)
                }
            assertTrue("Dialog must have space above it: $bounds", bounds.top > 24)
            val downTime = SystemClock.uptimeMillis()
            listOf(MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP).forEach { action ->
                val event = MotionEvent.obtain(downTime, SystemClock.uptimeMillis(), action, bounds.exactCenterX(), bounds.top - 24f, 0)
                try {
                    event.source = InputDevice.SOURCE_TOUCHSCREEN
                    assertTrue(instrumentation.uiAutomation.injectInputEvent(event, true))
                } finally { event.recycle() }
            }
            rule.waitForIdle()
        }
        touchOutside()
        rule.runOnIdle { assertEquals(0, dismissed.get()); state.value = state.value.copy(isVerifying = false) }
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsEnabled()
        touchOutside()
        rule.waitUntil(5_000) { dismissed.get() == 1 }
        rule.runOnIdle { assertEquals(1, dismissed.get()) }
    }

    @Test fun switchingBlocksCancelAndSystemBackThenRestoresSelection() {
        val state = mutableStateOf(ServerCenterUiState(hosts = listOf(
            ServerHostTarget("other", "Other host", "review.invalid", 22, "review", null, null, null, 0, 0)
        ), isVerifying = true))
        val closed = mutableStateOf(false)
        val selected = mutableListOf<String>()
        rule.setContent { MaterialTheme {
            if (closed.value) Text("Parent page")
            else SshHostSwitcherContent("current", state.value, true, { selected += it }, { closed.value = true }, {}, {})
        } }
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.server_center_switching_host)).assertIsDisplayed()
        rule.onAllNodesWithText(text(R.string.server_center_connect)).assertCountEquals(0)
        Espresso.pressBack()
        rule.runOnIdle { assertFalse(closed.value); assertTrue(selected.isEmpty()); state.value = state.value.copy(isVerifying = false, quickManaging = true) }
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsNotEnabled()
        Espresso.pressBack()
        rule.runOnIdle { assertFalse(closed.value); state.value = state.value.copy(quickManaging = false,
            verification = ServerCenterSshVerification.Failed(SshFailureReason.TimedOut)) }
        rule.onNodeWithText(text(R.string.server_center_ssh_failed_timeout)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.server_center_connect)).performClick()
        rule.runOnIdle { assertEquals(listOf("other"), selected); assertFalse(closed.value) }
        rule.onNodeWithText(text(R.string.common_cancel)).assertIsEnabled()
        Espresso.pressBack()
        rule.onNodeWithText("Parent page").assertIsDisplayed()
    }
}
