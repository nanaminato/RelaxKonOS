package app.relaxkonos.mobile.ui.connect

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class LoginPageLayoutTest {
    @get:Rule val rule = createComposeRule()

    @Test fun expandedTunnelFormScrollsOnPhone() = checkViewport(360, 640)
    @Test fun expandedTunnelFormScrollsInKeyboardSizedViewport() = checkViewport(360, 320)
    @Test fun expandedTunnelFormScrollsOnTablet() = checkViewport(800, 600)

    private fun checkViewport(width: Int, height: Int) {
        val expanded = mutableStateOf(false)
        var connected = false
        rule.setContent {
            MaterialTheme {
                Box(Modifier.requiredSize(width.dp, height.dp)) {
                    LoginPageLayout {
                        Column(Modifier.widthIn(max = 520.dp).fillMaxWidth()) {
                            Text("Server login")
                            Button(onClick = { expanded.value = !expanded.value }) { Text("Configure SSH") }
                            if (expanded.value) {
                                repeat(18) { index ->
                                    OutlinedTextField(value = "", onValueChange = {}, label = { Text("SSH option $index") })
                                }
                            }
                            Button(onClick = { connected = true }, modifier = Modifier.testTag("connect")) { Text("Connect") }
                        }
                    }
                }
            }
        }
        rule.onNodeWithText("Configure SSH").performClick()
        rule.onNodeWithTag("login-page-scroll").performTouchInput { swipeUp() }
        val range = rule.onNodeWithTag("login-page-scroll").fetchSemanticsNode().config[SemanticsProperties.VerticalScrollAxisRange]
        rule.runOnIdle {
            assertTrue("Expanding SSH options must leave a scrollable viewport", range.value() > 0f)
        }
        rule.onNodeWithTag("connect").performScrollTo().assertIsDisplayed().performClick()
        rule.runOnIdle { assertTrue("The connection action must remain reachable", connected) }
        rule.onNodeWithText("Configure SSH").performScrollTo().performClick()
        rule.onNodeWithTag("connect").performScrollTo().assertIsDisplayed()
    }
}
