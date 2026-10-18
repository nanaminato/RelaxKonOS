package app.relaxkonos.mobile.ui.connect

import android.util.Base64
import androidx.activity.ComponentActivity
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import androidx.test.espresso.Espresso
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.ui.common.LocalAppContainer
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class OwnerDevicePairingFlowTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private fun payload(server: String) = Base64.encodeToString(
        """{"version":1,"serverUrl":"https://$server.invalid","token":"isolated-review","expiresAt":"2099-01-01T00:00:00Z"}""".toByteArray(), Base64.NO_WRAP)
    private val code = mutableStateOf(payload("first"))
    private val busy = mutableStateOf(false)
    private val scanning = mutableStateOf(false)
    private val visible = mutableStateOf(true)
    private val submitted = mutableListOf<String>()
    private var closes = 0
    private var cameras = 0
    private fun text(id: Int) = rule.activity.getString(id)
    private fun show() {
        val container = (rule.activity.application as RelaxKonApplication).container
        rule.setContent { CompositionLocalProvider(LocalAppContainer provides container) { MaterialTheme {
            if (visible.value) OwnerDevicePairingContent(
                code = { code.value }, busy = { busy.value }, scanning = { scanning.value },
                message = null, scanMessage = null, onDismissMessage = {}, onDismissScanMessage = {},
                onCodeChange = { code.value = it }, onPair = { submitted += it },
                onCamera = { cameras++; scanning.value = true }, onImage = { scanning.value = true },
                onClose = { closes++; visible.value = false },
            )
        } } }
    }
    private fun open() {
        rule.onNodeWithText(text(R.string.owner_device_pair_next)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.owner_device_confirm_title)).assertIsDisplayed()
    }
    private fun callback(id: Int): () -> Boolean = requireNotNull(rule.onNodeWithText(text(id))
        .fetchSemanticsNode().config[SemanticsActions.OnClick].action)
    @Test fun confirmationConsumesOnlyItsDisplayedPayloadOnce() {
        show(); open()
        val confirm = callback(R.string.owner_device_confirm)
        rule.runOnIdle { confirm(); confirm(); assertEquals(listOf(code.value), submitted) }
        rule.onNodeWithText(text(R.string.owner_device_confirm_title)).assertDoesNotExist()
    }
    @Test fun changedInputBeforeRecompositionCannotSubmitAnotherInvitation() {
        show(); open(); val old = callback(R.string.owner_device_confirm)
        rule.mainClock.autoAdvance = false
        try { rule.runOnUiThread { code.value = payload("second"); old(); assertTrue(submitted.isEmpty()) } }
        finally { rule.mainClock.autoAdvance = true }
        open(); rule.runOnIdle { old(); assertTrue(submitted.isEmpty()) }
        val fresh = callback(R.string.owner_device_confirm)
        rule.runOnIdle { fresh(); fresh(); assertEquals(listOf(payload("second")), submitted) }
    }
    @Test fun pausedConfirmationRejectsOldCallbackAndRequiresNewReview() {
        show(); open(); val old = callback(R.string.owner_device_confirm)
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { old(); assertTrue(submitted.isEmpty()) }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.onNodeWithText(text(R.string.owner_device_confirm_title)).assertDoesNotExist()
        open(); rule.runOnIdle { old(); assertTrue(submitted.isEmpty()) }
        val fresh = callback(R.string.owner_device_confirm)
        rule.runOnIdle { fresh(); fresh(); assertEquals(1, submitted.size) }
    }
    @Test fun leavingBeforeRecompositionRejectsOriginalConfirmation() {
        show()
        val back = requireNotNull(rule.onNodeWithContentDescription(text(R.string.common_back))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action)
        open(); val old = callback(R.string.owner_device_confirm)
        rule.mainClock.autoAdvance = false
        try { rule.runOnUiThread { back(); old(); assertEquals(1, closes); assertTrue(submitted.isEmpty()) } }
        finally { rule.mainClock.autoAdvance = true }
    }
    @Test fun enrollmentBlocksSystemAndHeaderBackAndAllInputs() {
        show(); rule.runOnIdle { busy.value = true }
        rule.onNodeWithText(text(R.string.owner_device_pair_next)).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.owner_device_scan_camera)).assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.owner_device_pairing_code)).assertIsNotEnabled()
        rule.onNodeWithContentDescription(text(R.string.common_back)).performClick()
        Espresso.pressBack()
        rule.runOnIdle { assertEquals(0, closes); assertTrue(submitted.isEmpty()); busy.value = false }
        rule.onNodeWithContentDescription(text(R.string.common_back)).performClick()
        rule.runOnIdle { assertEquals(1, closes) }
    }
    @Test fun scanLaunchBlocksDuplicateCallbackAndNextUntilCancelled() {
        show()
        val camera = callback(R.string.owner_device_scan_camera)
        rule.mainClock.autoAdvance = false
        try { rule.runOnUiThread { camera(); camera(); assertEquals(1, cameras) } }
        finally { rule.mainClock.autoAdvance = true }
        rule.onNodeWithText(text(R.string.owner_device_pair_next)).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.owner_device_pairing_code)).assertIsNotEnabled()
        rule.runOnIdle { scanning.value = false }
        open(); rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertTrue(submitted.isEmpty()) }
    }
}
