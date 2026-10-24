package app.relaxkonos.mobile.ui.manage.tunnels

import androidx.compose.material3.MaterialTheme
import androidx.activity.ComponentActivity
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class TunnelProfileEditorTest {
    @get:Rule(order = 0) val diagnostics = app.relaxkonos.mobile.testing.ReviewWaitDiagnostics()
    @get:Rule(order = 1) val rule = createAndroidComposeRule<ComponentActivity>()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)
    private val draft = TunnelProfileDraft(name = "review-profile", host = "frp.example.com")

    private fun confirmButton() = rule.onAllNodesWithText(text(R.string.tunnels_confirm)).filter(hasClickAction()).onLast()
    private fun action(node: SemanticsNodeInteraction) = requireNotNull(node.fetchSemanticsNode()
        .config[androidx.compose.ui.semantics.SemanticsActions.OnClick].action)

    @Test fun oldSaveCannotReviewChangedDraft() {
        val state = mutableStateOf(TunnelsState(profileDraft = draft, initialProfile = draft))
        rule.setContent { MaterialTheme {
            TunnelProfileEditor(state.value, { state.value = state.value.copy(profileDraft = it) }, {}, {}, {}, {}, { !state.value.busy })
        } }
        val oldSave = action(rule.onNodeWithText(text(R.string.common_save)))
        rule.onNodeWithText(text(R.string.tunnels_name)).performTextReplacement("current-profile")
        Espresso.closeSoftKeyboard()
        rule.runOnIdle { oldSave() }
        rule.onNodeWithText(text(R.string.tunnels_profile_save_confirm)).assertDoesNotExist()
    }

    @Test fun oldDismissCannotConsumeReopenedSaveAndCurrentConfirmationSendsOnce() {
        val state = mutableStateOf(TunnelsState(profileDraft = draft, initialProfile = draft))
        var writes = 0
        rule.setContent { MaterialTheme {
            TunnelProfileEditor(state.value, { state.value = state.value.copy(profileDraft = it) }, {}, { writes++ }, {}, {}, { !state.value.busy })
        } }
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        val oldDismiss = action(rule.onNodeWithText(text(R.string.common_cancel)))
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        rule.runOnIdle { oldDismiss(); assertEquals(0, writes) }
        rule.onNodeWithText(text(R.string.tunnels_profile_save_confirm)).assertIsDisplayed()
        val submit = action(confirmButton())
        rule.runOnIdle { submit(); submit(); assertEquals(1, writes) }
    }

    @Test fun replacingDraftRevokesOriginalSaveConfirmation() {
        val state = mutableStateOf(TunnelsState(profileDraft = draft, initialProfile = draft))
        var writes = 0
        rule.setContent { MaterialTheme {
            TunnelProfileEditor(state.value, { state.value = state.value.copy(profileDraft = it) }, {}, { writes++ }, {}, {}, { !state.value.busy })
        } }
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        val oldSubmit = action(confirmButton())
        rule.runOnIdle { state.value = state.value.copy(profileDraft = draft.copy(name = "replacement")) }
        rule.onNodeWithText(text(R.string.tunnels_profile_save_confirm)).assertDoesNotExist()
        rule.runOnIdle { oldSubmit(); assertEquals(0, writes) }
    }

    @Test fun reopenedDiscardRejectsOldContinueAndDiscardAndConsumesCurrentOnce() {
        val state = mutableStateOf(TunnelsState(profileDraft = draft.copy(name = "edited"), initialProfile = draft))
        var closed = 0
        rule.setContent { MaterialTheme {
            TunnelProfileEditor(state.value, { state.value = state.value.copy(profileDraft = it) }, { closed++ }, {}, {}, {}, { !state.value.busy })
        } }
        rule.onNodeWithText(text(R.string.common_close)).performClick()
        val oldKeep = action(rule.onNodeWithText(text(R.string.common_cancel)))
        val oldDiscard = action(confirmButton())
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText(text(R.string.common_close)).performClick()
        rule.runOnIdle { oldKeep(); oldDiscard(); assertEquals(0, closed) }
        rule.onNodeWithText(text(R.string.tunnels_discard_confirm)).assertIsDisplayed()
        val current = action(confirmButton())
        rule.runOnIdle { current(); current(); assertEquals(1, closed) }
    }

    @Test fun reopenedReloadRejectsOldDismissAndSubmitAndQueriesOnlyOnce() {
        val saved = draft.copy(id = "22222222-2222-2222-2222-222222222222", revision = 1)
        val state = mutableStateOf(TunnelsState(profileDraft = saved, initialProfile = saved))
        var reloads = 0
        rule.setContent { MaterialTheme {
            TunnelProfileEditor(state.value, { state.value = state.value.copy(profileDraft = it) }, {}, {}, { reloads++ }, {}, { !state.value.busy })
        } }
        rule.onNodeWithText(text(R.string.tunnels_reload)).performScrollTo().performClick()
        val oldDismiss = action(rule.onNodeWithText(text(R.string.common_cancel)))
        val oldSubmit = action(confirmButton())
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText(text(R.string.tunnels_reload)).performScrollTo().performClick()
        rule.runOnIdle { oldDismiss(); oldSubmit(); assertEquals(0, reloads) }
        rule.onNodeWithText(text(R.string.tunnels_reload_confirm)).assertIsDisplayed()
        val current = action(confirmButton())
        rule.runOnIdle { current(); current(); assertEquals(1, reloads) }
    }

    @Test fun profileResumeRejectsOldFieldsSaveCloseReloadAndProbe() {
        val saved = draft.copy(id = "22222222-2222-2222-2222-222222222222", revision = 1,
            mode = app.relaxkonos.mobile.core.net.TunnelRuntimeMode.External, path = "/opt/frpc")
        val state = mutableStateOf(TunnelsState(profileDraft = saved, initialProfile = saved))
        var writes = 0; var reloads = 0; var probes = 0; var closes = 0
        rule.setContent { MaterialTheme {
            TunnelProfileEditor(state.value, { state.value = state.value.copy(profileDraft = it) }, { closes++ }, { writes++ },
                { reloads++ }, { probes++ }, { !state.value.busy })
        } }
        val field = requireNotNull(rule.onNode(hasText(text(R.string.tunnels_name)) and hasSetTextAction())
            .fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsActions.SetText].action)
        val save = action(rule.onNodeWithText(text(R.string.common_save)))
        val close = action(rule.onNodeWithText(text(R.string.common_close)))
        val reload = action(rule.onNodeWithText(text(R.string.tunnels_reload)).performScrollTo())
        val probe = action(rule.onNodeWithText(text(R.string.tunnels_probe)).performScrollTo())
        fun oldCallbacks() {
            field(androidx.compose.ui.text.AnnotatedString("stale")); save(); close(); reload(); probe()
            assertSame(saved, state.value.profileDraft); assertEquals(0, writes); assertEquals(0, reloads); assertEquals(0, probes); assertEquals(0, closes)
        }
        val resumed = java.util.concurrent.atomic.AtomicBoolean()
        val observer = LifecycleEventObserver { _, event -> if (event == Lifecycle.Event.ON_RESUME) { oldCallbacks(); resumed.set(true) } }
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread { oldCallbacks(); rule.activity.lifecycle.addObserver(observer) }
            rule.mainClock.autoAdvance = true
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
            rule.waitUntil(5_000) { resumed.get() }
        } finally {
            rule.mainClock.autoAdvance = true
            rule.runOnUiThread { rule.activity.lifecycle.removeObserver(observer) }
        }
        rule.runOnIdle { oldCallbacks() }
        rule.onNodeWithText(text(R.string.tunnels_reload_confirm)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.tunnels_profile_save_confirm)).assertDoesNotExist()
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        val current = action(confirmButton())
        rule.runOnIdle { current(); current(); assertEquals(1, writes) }
    }

    @Test fun failedSaveKeepsDraftAndShowsReasonInsideEditor() {
        val state = mutableStateOf(TunnelsState(profileDraft = draft, initialProfile = draft))
        var submitted = 0
        rule.setContent { MaterialTheme {
            TunnelProfileEditor(state.value, { state.value = state.value.copy(profileDraft = it) }, {},
                { submitted++; state.value = state.value.copy(busy = true) }, {}, {}, { !state.value.busy })
        } }
        rule.onNodeWithText(text(R.string.tunnels_name)).performTextReplacement("edited-profile")
        Espresso.closeSoftKeyboard()
        rule.onNodeWithText(text(R.string.common_save)).performClick()
        rule.onAllNodesWithText(text(R.string.tunnels_confirm)).filter(hasClickAction()).onLast().performClick()
        rule.onNodeWithText(text(R.string.tunnels_name)).assertIsNotEnabled()
        rule.runOnIdle { assertEquals(1, submitted); state.value = state.value.copy(busy = false, problemCode = "tunnel.revision_conflict") }
        rule.onNodeWithText(text(R.string.tunnels_conflict)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("edited-profile").assertExists()
        rule.runOnIdle { state.value = state.value.copy(problemCode = null, uncertain = true) }
        rule.onNodeWithText(text(R.string.tunnels_uncertain)).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText("edited-profile").assertExists()
    }

    @Test fun dirtyBackCanCancelOrExplicitlyDiscard() {
        val state = mutableStateOf(TunnelsState(profileDraft = draft, initialProfile = draft))
        var closed = false
        rule.setContent { MaterialTheme {
            if (!closed) TunnelProfileEditor(state.value, { state.value = state.value.copy(profileDraft = it) },
                { closed = true }, {}, {}, {}, { !state.value.busy })
        } }
        rule.onNodeWithText(text(R.string.tunnels_name)).performTextReplacement("edited-profile")
        Espresso.closeSoftKeyboard()
        Espresso.pressBack()
        rule.onNodeWithText(text(R.string.tunnels_discard_confirm)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_cancel)).performClick()
        rule.onNodeWithText("edited-profile").assertExists()
        rule.runOnIdle { assertFalse(closed) }
        Espresso.pressBack()
        rule.onAllNodesWithText(text(R.string.tunnels_confirm)).filter(hasClickAction()).onLast().performClick()
        rule.runOnIdle { assertTrue(closed) }
    }
}
