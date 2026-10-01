package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class NativeTerminalTest {
    @get:Rule val rule = createComposeRule()

    @Test fun connectionCompletionAndViewportChangeSynchronizeNativePtySize() {
        val connected = mutableStateOf(false)
        val width = mutableStateOf(320.dp)
        val sizes = mutableListOf<Pair<Int, Int>>()
        rule.setContent {
            MaterialTheme {
                Box(Modifier.requiredSize(width.value, 240.dp)) {
                    NativeTerminal("shell", "中> prompt", 13,
                        { columns, rows -> if (connected.value) sizes += columns to rows },
                        connected = connected.value)
                }
            }
        }
        rule.onNodeWithText("中> prompt").assertIsDisplayed()
        rule.runOnIdle { assertTrue(sizes.isEmpty()); connected.value = true }
        rule.waitUntil { sizes.isNotEmpty() }
        val initial = sizes.last()
        rule.runOnIdle { width.value = 700.dp }
        rule.waitUntil { sizes.last().first > initial.first }
        assertTrue(sizes.last().second == initial.second)
    }
}
