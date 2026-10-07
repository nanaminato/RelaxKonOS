package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.width
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class SelectionOptionsTest {
    @get:Rule val rule = createComposeRule()

    @Test fun checkboxLabelIsOneNamedActionAndTogglesExactlyOnce() {
        val checked = mutableStateOf(false)
        var changes = 0
        rule.setContent { MaterialTheme {
            CheckboxOption(checked.value, "Save this password") { checked.value = it; changes++ }
        } }
        rule.onAllNodes(hasClickAction()).assertCountEquals(1)
        rule.onNodeWithText("Save this password").assertIsOff()
            .assert(SemanticsMatcher.expectValue(SemanticsProperties.Role, Role.Checkbox))
        rule.onNodeWithText("Save this password", useUnmergedTree = true).performClick()
        rule.onNodeWithText("Save this password").assertIsOn()
        rule.runOnIdle { assertEquals(1, changes) }
    }

    @Test fun disabledRadioLabelCannotChangeSelection() {
        var changes = 0
        rule.setContent { MaterialTheme { RadioOption(false, "Install package", false) { changes++ } } }
        rule.onNodeWithText("Install package").assertIsNotEnabled().assertIsNotSelected()
            .assert(SemanticsMatcher.expectValue(SemanticsProperties.Role, Role.RadioButton)).performClick()
        rule.runOnIdle { assertEquals(0, changes) }
    }

    @Test fun wrappedLargeJapaneseLabelStaysCompleteAndClickable() {
        val checked = mutableStateOf(false)
        val label = "この端末にパスワードを保存し、指紋認証を使用して保護されたパスワードを解除します。"
        rule.setContent { MaterialTheme {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.8f)) {
                Box(Modifier.width(320.dp)) { CheckboxOption(checked.value, label) { checked.value = it } }
            }
        } }
        val layouts = mutableListOf<TextLayoutResult>()
        rule.onNodeWithText(label, useUnmergedTree = true).performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
        assertTrue(layouts.single().lineCount > 1)
        assertFalse(layouts.single().hasVisualOverflow)
        rule.onNodeWithText(label, useUnmergedTree = true).performClick()
        rule.onNodeWithText(label).assertIsOn()
    }
}
