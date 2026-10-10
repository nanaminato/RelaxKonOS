package app.relaxkonos.mobile.ui.manage.smb

import android.accessibilityservice.AccessibilityServiceInfo
import android.graphics.Bitmap
import android.graphics.Rect
import android.os.SystemClock
import android.view.InputDevice
import android.view.MotionEvent
import android.view.accessibility.AccessibilityWindowInfo
import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import java.io.File
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SmbConfirmationDialogTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private val facts = SmbFacts(SmbCapabilities(true, true, true, true, false, null),
        SmbStatus(SmbRuntimeState.Running, "4", true, true, null), emptyList(), listOf(SmbUser("review-user", true, true)),
        SmbConnection("review-host", 445, "\\\\review-host", "smb://review-host"))
    private val pending = mutableStateOf(SmbConfirmation(facts, SmbChange(SmbChangeKind.Password, "review-user")))
    private val visible = mutableStateOf(true)
    private val busy = mutableStateOf(false)
    private val ready = mutableStateOf(true)
    private var sends = 0
    private var closes = 0
    private var receivedTarget: String? = null
    private var received: CharArray? = null
    private val sample = "synthetic-review-password"
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private fun field(label: Int) = rule.onNode(SemanticsMatcher.keyIsDefined(SemanticsProperties.EditableText) and hasText(text(label)))
    private fun action(label: Int) = rule.onNode(hasClickAction() and hasText(text(label)))
    private fun emptyFields() {
        listOf(R.string.smb_password, R.string.smb_password_again).forEach { label ->
            field(label).assert(SemanticsMatcher("empty secret input") { it.config[SemanticsProperties.EditableText].text.isEmpty() })
        }
        action(R.string.smb_submit).assertIsNotEnabled()
    }
    private fun show() {
        rule.runOnUiThread { rule.activity.window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE) }
        rule.setContent { MaterialTheme {
            if (visible.value) SmbConfirmationDialog(pending.value, busy.value, ready.value, {
                closes++; visible.value = false
            }) { secret ->
                sends++; receivedTarget = pending.value.change.target; received = secret; visible.value = false
            } else Text("Parent page")
        } }
    }
    private fun fill(value: String = sample, again: String = value) {
        field(R.string.smb_password).performScrollTo().performTextReplacement(value)
        field(R.string.smb_password_again).performScrollTo().performTextReplacement(again)
    }
    private fun withKeyboard(block: () -> Unit) {
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        val info = automation.serviceInfo
        val oldFlags = info.flags
        info.flags = info.flags or AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
        automation.serviceInfo = info
        try {
            field(R.string.smb_password_again).performScrollTo().performClick()
            rule.waitUntil(10_000) { automation.windows.any { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD } }
            rule.waitForIdle()
            SystemClock.sleep(500)
            block()
        } finally {
            info.flags = oldFlags
            automation.serviceInfo = info
            received?.fill('\u0000')
        }
    }
    private fun screenshot(name: String) {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        File(instrumentation.targetContext.getExternalFilesDir(null), name).outputStream().use {
            instrumentation.uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, it)
        }
    }
    private fun tapAboveKeyboard(label: Int) {
        val node = action(label).assertIsDisplayed()
        val bounds = node.fetchSemanticsNode().boundsInWindow
        val origin = IntArray(2)
        Espresso.onView(androidx.test.espresso.matcher.ViewMatchers.isRoot())
            .inRoot(object : org.hamcrest.TypeSafeMatcher<androidx.test.espresso.Root>() {
                override fun describeTo(description: org.hamcrest.Description) { description.appendText("focused SMB confirmation") }
                override fun matchesSafely(root: androidx.test.espresso.Root) =
                    androidx.test.espresso.matcher.RootMatchers.isDialog().matches(root) && root.decorView.hasWindowFocus()
            }).check { view, failure ->
                if (failure != null) throw failure
                val inWindow = IntArray(2)
                view.getLocationOnScreen(origin); view.getLocationInWindow(inWindow)
                origin[0] -= inWindow[0]; origin[1] -= inWindow[1]
            }
        val screen = Rect(bounds.left.toInt() + origin[0], bounds.top.toInt() + origin[1],
            bounds.right.toInt() + origin[0], bounds.bottom.toInt() + origin[1])
        val automation = InstrumentationRegistry.getInstrumentation().uiAutomation
        val keyboard = Rect().also(automation.windows.single { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD }::getBoundsInScreen)
        assertFalse(screen.isEmpty)
        assertTrue("SMB action $screen must be fully above $keyboard", screen.top >= 0 && screen.bottom <= keyboard.top)
        val down = SystemClock.uptimeMillis()
        listOf(MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP).forEach { kind ->
            val event = MotionEvent.obtain(down, SystemClock.uptimeMillis(), kind, screen.exactCenterX(), screen.exactCenterY(), 0)
            try { event.source = InputDevice.SOURCE_TOUCHSCREEN; assertTrue(automation.injectInputEvent(event, true)) }
            finally { event.recycle() }
        }
    }

    @Test fun realKeyboardCancelIsVisibleAndReopeningHasNoSecret() {
        show(); fill()
        withKeyboard {
            screenshot("smb-password-cancel-keyboard.png")
            tapAboveKeyboard(R.string.common_cancel)
            rule.onNodeWithText("Parent page").assertIsDisplayed()
            rule.runOnIdle { assertEquals(1, closes); assertEquals(0, sends); visible.value = true }
            emptyFields()
            rule.onNodeWithText("review-user").assertExists()
        }
    }

    @Test fun realKeyboardSubmitIsVisibleAndTransfersOnlyThisTargetsSecretOnce() {
        show(); fill()
        withKeyboard {
            screenshot("smb-password-submit-keyboard.png")
            tapAboveKeyboard(R.string.smb_submit)
            rule.onNodeWithText("Parent page").assertIsDisplayed()
            rule.runOnIdle {
                assertEquals(1, sends); assertEquals(0, closes); assertEquals("review-user", receivedTarget)
                assertArrayEquals(sample.toCharArray(), received)
                received?.fill('\u0000'); visible.value = true
            }
            emptyFields()
        }
    }

    @Test fun validationBusyStateAndTargetChangesNeverReuseAnOldSecret() {
        show(); emptyFields()
        fill("short")
        action(R.string.smb_submit).assertIsNotEnabled()
        fill(sample, "different-password")
        action(R.string.smb_submit).assertIsNotEnabled()
        fill("review-password\u0001")
        action(R.string.smb_submit).assertIsNotEnabled()
        fill()
        action(R.string.smb_submit).assertIsEnabled()
        rule.runOnIdle { busy.value = true; ready.value = false }
        field(R.string.smb_password).assertIsNotEnabled()
        field(R.string.smb_password_again).assertIsNotEnabled()
        action(R.string.smb_submit).assertIsNotEnabled()
        action(R.string.common_cancel).assertIsEnabled()
        rule.runOnIdle { busy.value = false; ready.value = true }
        action(R.string.smb_submit).assertIsEnabled()
        rule.runOnIdle { pending.value = pending.value.copy(change = SmbChange(SmbChangeKind.Password, "other-user")) }
        emptyFields()
        rule.onNodeWithText("other-user").assertExists()
        Espresso.closeSoftKeyboard()
        Espresso.pressBack()
        rule.onNodeWithText("Parent page").assertIsDisplayed()
        rule.runOnIdle { assertEquals(0, sends); assertEquals(1, closes); visible.value = true }
        emptyFields()
    }

    @Test fun longShareSummaryCancelKeepsFrozenDraftAndDoesNotSubmit() {
        val request = SmbShareRequest("review-share", "/tmp/review-share", null, false, true, true,
            (1..40).map { SmbPermission("review-user-$it", SmbAccess.Read) })
        pending.value = SmbConfirmation(facts, SmbChange(SmbChangeKind.CreateShare, share = request))
        show()
        rule.onNodeWithText("review-share").assertExists()
        rule.onNodeWithText(text(R.string.smb_path_warning)).assertExists()
        rule.onNodeWithText("review-user-40 · " + text(R.string.smb_access_read)).performScrollTo().assertIsDisplayed()
        action(R.string.common_cancel).performClick()
        rule.onNodeWithText("Parent page").assertIsDisplayed()
        rule.runOnIdle { assertEquals(request, pending.value.change.share); assertEquals(0, sends); assertEquals(1, closes) }
    }
}
