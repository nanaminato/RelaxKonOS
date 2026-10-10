package app.relaxkonos.mobile.ui.manage.websites

import android.graphics.Rect
import android.os.SystemClock
import android.view.InputDevice
import android.view.MotionEvent
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.espresso.matcher.RootMatchers.isDialog
import androidx.test.espresso.matcher.ViewMatchers.isRoot
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.HostOperatingSystemKind
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class NginxInstallFormTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun busyFormBlocksBackAndOutsideThenAllowsCloseWithPendingIntent() {
        val state = mutableStateOf(NginxState(system = HostOperatingSystemKind.Ubuntu, busy = true))
        var closed = 0
        var installs = 0
        rule.setContent { MaterialTheme {
            Text("Parent page")
            NginxInstallForm(state.value, true, null, false, {}, {}, {}, {}, { _, _ -> installs++ }, { closed++ })
        } }
        rule.onNodeWithText(text(R.string.common_close)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.common_retry)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.nginx_install_confirm)).assertIsNotEnabled()
        Espresso.pressBackUnconditionally()
        touchOutside()
        rule.runOnIdle {
            assertEquals(0, closed); assertEquals(0, installs)
            state.value = state.value.copy(busy = false, uncertain = true, pendingInstallation = true)
        }
        rule.onNodeWithText(text(R.string.nginx_uncertain)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_close)).assertIsEnabled().performClick()
        rule.runOnIdle { assertEquals(1, closed); assertEquals(0, installs) }
    }

    @Test fun unknownSubmissionRetainsConfirmationAndAllowsExplicitRetryWithoutReplay() {
        val state = mutableStateOf(NginxState(system = HostOperatingSystemKind.Windows10))
        val hasIntent = mutableStateOf(false)
        val installs = mutableListOf<Pair<String?, Boolean>>()
        var closed = 0
        rule.setContent { MaterialTheme {
            NginxInstallForm(state.value, hasIntent.value, null, false, {}, {}, {}, {}, { version, source ->
                installs += version to source
                hasIntent.value = true
                state.value = state.value.copy(busy = true)
            }, { closed++ })
        } }
        rule.onNodeWithText(text(R.string.nginx_version)).performScrollTo().performTextInput("1.27.5")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.nginx_install_confirm)).performScrollTo().performClick()
        rule.onNode(hasText(text(R.string.nginx_install)) and hasClickAction()).assertIsEnabled().performClick()
        rule.onNodeWithText(text(R.string.common_retry)).assertIsNotEnabled()
        Espresso.pressBackUnconditionally()
        rule.runOnIdle {
            assertEquals(listOf("1.27.5" to false), installs); assertEquals(0, closed)
            state.value = state.value.copy(busy = false, uncertain = true, pendingInstallation = true)
        }
        rule.onNodeWithText(text(R.string.nginx_uncertain)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("1.27.5").performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.nginx_install_confirm)).performScrollTo().assertIsOn()
        rule.onNodeWithText(text(R.string.common_retry)).assertIsEnabled()
        rule.runOnIdle { assertEquals(1, installs.size); assertEquals(0, closed) }
        rule.onNodeWithText(text(R.string.common_retry)).performClick()
        rule.runOnIdle { assertEquals(listOf("1.27.5" to false, "1.27.5" to false), installs); assertEquals(0, closed) }
    }

    @Test fun explicitFailureIsVisibleInFormAndAllowsBackWithoutSubmission() {
        var closed = 0
        var installs = 0
        rule.setContent { MaterialTheme {
            NginxInstallForm(NginxState(system = HostOperatingSystemKind.Ubuntu,
                problemCode = "webserver.install_elevation_required"), false, null, false,
                {}, {}, {}, {}, { _, _ -> installs++ }, { closed++ })
        } }
        rule.onNodeWithText(text(R.string.error_elevation_required)).assertIsDisplayed()
        rule.onNode(hasText(text(R.string.nginx_install)) and hasClickAction()).assertIsNotEnabled()
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(1, closed); assertEquals(0, installs) }
    }

    private fun touchOutside() {
        val bounds = Rect()
        Espresso.onView(isRoot()).inRoot(isDialog()).check { view, failure ->
            if (failure != null) throw failure
            val location = IntArray(2)
            view.getLocationOnScreen(location)
            bounds.set(location[0], location[1], location[0] + view.width, location[1] + view.height)
        }
        assertTrue("Dialog needs space for outside touch: $bounds", bounds.top > 24)
        val down = SystemClock.uptimeMillis()
        listOf(MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP).forEach { action ->
            val event = MotionEvent.obtain(down, SystemClock.uptimeMillis(), action, bounds.exactCenterX(), bounds.top - 24f, 0)
            try {
                event.source = InputDevice.SOURCE_TOUCHSCREEN
                assertTrue(InstrumentationRegistry.getInstrumentation().uiAutomation.injectInputEvent(event, true))
            } finally { event.recycle() }
        }
        rule.waitForIdle()
    }
}
