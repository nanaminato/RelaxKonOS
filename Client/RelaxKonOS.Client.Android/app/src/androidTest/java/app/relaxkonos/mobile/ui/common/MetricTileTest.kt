package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
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

class MetricTileTest {
    @get:Rule val rule = createComposeRule()

    @Test fun narrowLargeFontMetricKeepsLongMountPointValueAndCapacityReadable() {
        val label = "Disk /srv/" + "long-mount-directory/".repeat(8)
        val value = "100.00 percent used"
        val supporting = "987.65 GiB used out of 1234.56 GiB total capacity"
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.5f)) {
                MaterialTheme {
                    Column(Modifier.width(140.dp).verticalScroll(rememberScrollState())) {
                        MetricTile(label, value, supporting = supporting, progress = 1f)
                    }
                }
            }
        }
        listOf(label, value, supporting).forEach { text ->
            val results = mutableListOf<TextLayoutResult>()
            rule.onNodeWithText(text).performScrollTo().assertIsDisplayed()
                .performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(results) }
            assertEquals(1, results.size)
            assertFalse("Metric text must remain complete: $text", results.single().hasVisualOverflow)
            assertTrue("Long metric text should wrap", results.single().lineCount > 1)
        }
    }
}
