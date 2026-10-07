package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.width
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class UiReviewLayoutTest {
    @get:Rule val rule = createComposeRule()

    @Test fun longDetailValueIsCompleteWithLargeText() {
        val path = "/srv/very-long-directory-name/".repeat(8) + "config.json"
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.6f)) {
                MaterialTheme { Box(Modifier.width(320.dp)) { KeyValueRow("Configuration path", path) } }
            }
        }
        val layouts = mutableListOf<TextLayoutResult>()
        rule.onNodeWithText(path).performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
        assertTrue(layouts.single().lineCount > 2)
        assertFalse("Detail values must not be clipped", layouts.single().hasVisualOverflow)
        assertFalse(layouts.single().isLineEllipsized(layouts.single().lineCount - 1))
    }

    @Test fun narrowCardKeepsTitleAndActionReachable() {
        var clicked = false
        rule.setContent {
            MaterialTheme {
                Box(Modifier.width(320.dp)) {
                    SectionCard("Connection diagnostics", trailing = {
                        TextButton(onClick = { clicked = true }) { Text("Run diagnostics") }
                    }) { Text("Results") }
                }
            }
        }
        val title = rule.onNodeWithText("Connection diagnostics").fetchSemanticsNode().boundsInRoot
        val action = rule.onNodeWithText("Run diagnostics").fetchSemanticsNode().boundsInRoot
        assertTrue("The action must use its own row on narrow cards", action.top >= title.bottom)
        rule.onNodeWithText("Run diagnostics").assertIsDisplayed().performClick()
        rule.runOnIdle { assertTrue(clicked) }
    }
}
