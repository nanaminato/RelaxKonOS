package app.relaxkonos.mobile.ui.files

import androidx.activity.compose.BackHandler
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.test.espresso.Espresso
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class FilesNavigationTest {
    @get:Rule val rule = createComposeRule()

    @Test fun selectionConsumesBackBeforeDirectoryNavigationAndAtRoot() {
        val selecting = mutableStateOf(true)
        val busy = mutableStateOf(true)
        val canGoUp = mutableStateOf(true)
        var selectionsClosed = 0
        var ups = 0
        var exits = 0
        rule.setContent {
            BackHandler { exits++ }
            FilesDirectoryBackHandler(canGoUp.value, busy.value, selecting.value,
                { selectionsClosed++; selecting.value = false }, { ups++; canGoUp.value = false })
            Text("Selected files")
        }
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(0, selectionsClosed); assertEquals(0, ups); busy.value = false }
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(1, selectionsClosed); assertEquals(0, ups); assertEquals(0, exits) }
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(1, ups); selecting.value = true }
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(2, selectionsClosed); assertEquals(0, exits) }
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(1, exits) }
    }

    @Test fun systemBackRespectsMutationThenGoesUpAndFallsThroughAtRoot() {
        val canGoUp = mutableStateOf(true)
        val busy = mutableStateOf(true)
        var ups = 0
        var exits = 0
        rule.setContent {
            BackHandler { exits++ }
            FilesDirectoryBackHandler(canGoUp.value, busy.value, false, {}) { ups++; canGoUp.value = false }
            Text("File browser")
        }
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(0, ups); assertEquals(0, exits); busy.value = false }
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(1, ups); assertEquals(0, exits) }
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(1, ups); assertEquals(1, exits); busy.value = true }
        Espresso.pressBackUnconditionally()
        rule.runOnIdle { assertEquals(1, ups); assertEquals(1, exits) }
    }
}
