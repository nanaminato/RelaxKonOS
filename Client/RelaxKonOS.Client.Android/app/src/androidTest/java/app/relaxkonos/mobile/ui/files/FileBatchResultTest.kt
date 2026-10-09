package app.relaxkonos.mobile.ui.files

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.data.FileBatchFailure
import app.relaxkonos.mobile.data.FileBatchReport
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class FileBatchResultTest {
    @get:Rule val rule = createComposeRule()
    private fun text(id: Int) = InstrumentationRegistry.getInstrumentation().targetContext.getString(id)

    @Test fun largeFontLongReportKeepsUnknownWarningAndExplicitCloseAndRefreshAccessible() {
        val completed = (1..15).map { "/review/" + "long-directory/".repeat(12) + "completed-$it" }
        val failure = "/review/unknown"
        val skipped = "/review/skipped"
        val report = FileBatchReport(17, completed,
            listOf(FileBatchFailure(failure, ApiResult.Transport("private transport details"), true)), listOf(skipped))
        val open = mutableStateOf(true)
        var refreshes = 0
        var closes = 0
        rule.setContent {
            val density = LocalDensity.current
            CompositionLocalProvider(LocalDensity provides Density(density.density, 1.5f)) { MaterialTheme {
                Text("Parent file browser")
                if (open.value) FileBatchResultDialog(report, { closes++; open.value = false }, { refreshes++ })
            } }
        }
        rule.onNodeWithText(text(R.string.files_batch_result)).assertIsDisplayed()
        rule.onNodeWithText("private transport details", substring = true).assertDoesNotExist()
        rule.onNodeWithText(skipped, substring = true).performScrollTo().assertIsDisplayed()
        rule.onNodeWithText(text(R.string.files_mutation_unknown)).performScrollTo().assertIsDisplayed()
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(0, closes); assertEquals(0, refreshes) }
        rule.onNodeWithText(text(R.string.common_refresh)).assertIsDisplayed().performClick()
        rule.runOnIdle { assertEquals(1, refreshes); assertEquals(0, closes) }
        rule.onNodeWithText(text(R.string.files_batch_result)).assertIsDisplayed()
        rule.onNodeWithText(text(R.string.common_close)).assertIsDisplayed().performClick()
        rule.runOnIdle { assertEquals(1, closes); assertEquals(1, refreshes) }
        rule.onNodeWithText(text(R.string.files_batch_result)).assertDoesNotExist()
        rule.onNodeWithText("Parent file browser").assertIsDisplayed()
    }
}
