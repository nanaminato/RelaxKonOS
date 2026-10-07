package app.relaxkonos.mobile.ui.manage.smb

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SmbRetainedShareEditorTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun unavailableFactsKeepDraftReadOnlyAndRefreshDoesNotDiscardIt() {
        val busy = mutableStateOf(false)
        val draft = SmbDraft(name = "review-share", path = "/srv/review-share", readOnly = true,
            permissions = listOf(SmbPermission("review-user", SmbAccess.Read)))
        val facts = SmbFacts(SmbCapabilities(true, true, true, true, false, null),
            SmbStatus(SmbRuntimeState.Running, null, true, true, null), emptyList(), emptyList(),
            SmbConnection("review-host", 445, "\\\\review-host", "smb://review-host"))
        var refreshed = 0
        var discarded = false
        rule.setContent { MaterialTheme { Column(Modifier.verticalScroll(rememberScrollState())) {
            SmbRetainedShareEditor(draft, facts, busy.value, { refreshed++; busy.value = true }, { discarded = true })
        } } }
        rule.onNodeWithText("review-share").assertExists()
        rule.onNodeWithText(text(R.string.smb_name)).assertIsNotEnabled()
        rule.onNodeWithText("/srv/review-share").assertExists()
        rule.onNodeWithText(text(R.string.common_save)).performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(text(R.string.smb_refresh_keep_draft)).performScrollTo().performClick()
        rule.onNodeWithText(text(R.string.smb_refresh_keep_draft)).assertIsNotEnabled()
        rule.onNodeWithText("review-share").assertExists()
        rule.runOnIdle { assertEquals(1, refreshed); assertFalse(discarded) }
    }
}
