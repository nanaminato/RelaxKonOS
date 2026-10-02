package app.relaxkonos.mobile.ui.manage.proxy

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.width
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class ProxyChoiceAlignmentTest {
    @get:Rule val rule = createComposeRule()

    @Test fun checkboxAndWrappedLabelShareVerticalCenterOnNarrowForms() {
        val label = InstrumentationRegistry.getInstrumentation().targetContext.getString(R.string.mihomo_system_proxy)
        rule.setContent { MaterialTheme {
            Column(Modifier.width(150.dp)) { ProxyCheck(false, true, R.string.mihomo_system_proxy) {} }
        } }
        val checkbox = rule.onNode(isToggleable(), useUnmergedTree = true).fetchSemanticsNode().boundsInRoot
        val text = rule.onNodeWithText(label, useUnmergedTree = true).fetchSemanticsNode().boundsInRoot
        assertEquals(checkbox.center.y, text.center.y, 1f)
    }
}
