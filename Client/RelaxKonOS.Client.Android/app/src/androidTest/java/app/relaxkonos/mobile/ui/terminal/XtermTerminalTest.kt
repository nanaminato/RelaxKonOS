package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.test.junit4.createComposeRule
import java.util.concurrent.atomic.AtomicReference
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test

class XtermTerminalTest {
    @get:Rule val rule = createComposeRule()

    @Test fun windowsRedrawSurvivesViewportResizeAndQueryReplies() {
        lateinit var view: XtermView
        val height = mutableStateOf(300.dp)
        val sent = mutableListOf<String>()
        val sizes = mutableListOf<Pair<Int, Int>>()
        val output = mutableStateOf("Microsoft Windows\r\nC:\\>")
        rule.setContent {
            AndroidView(factory = { XtermView(it).also { created ->
                view = created
                created.bridge.send = { sent += it }
                created.bridge.resize = { cols, rows -> sizes += cols to rows }
            } }, modifier = Modifier.width(320.dp).height(height.value),
                onRelease = { it.release() }, update = { it.update(output.value, 13f, true) })
        }
        fun screen(): String {
            val result = AtomicReference<String?>()
            rule.runOnIdle { view.evaluateJavascript(
                "typeof terminal === 'undefined' ? '' : Array.from({length:terminal.rows},(_,i)=>terminal.buffer.active.getLine(terminal.buffer.active.baseY+i)?.translateToString(true)).join('\\n')",
                { result.set(it) }) }
            rule.waitUntil(5000) { result.get() != null }
            return result.get().orEmpty()
        }
        rule.waitUntil(10000) { screen().contains("Microsoft Windows") }
        val fillsViewport = AtomicReference<String?>()
        rule.runOnIdle { view.evaluateJavascript(
            "document.getElementById('terminal').clientHeight >= innerHeight - 1 && terminal.rows > 2",
            { fillsViewport.set(it) }) }
        rule.waitUntil(5000) { fillsViewport.get() != null }
        assertEquals("true", fillsViewport.get())
        fun assertScreenFits() {
            // Text can render before the 200ms fit debounce and before the next browser paint.
            // Wait for the actual screen to fit; a persistent overflow still fails this assertion.
            rule.waitUntil(5000) {
                val fits = AtomicReference<String?>()
                rule.runOnIdle { view.evaluateJavascript(
                    "(()=>{const box=document.querySelector('.xterm-screen').getBoundingClientRect();return box.bottom<=innerHeight && box.right<=innerWidth;})()",
                    { fits.set(it) }) }
                rule.waitUntil(5000) { fits.get() != null }
                fits.get() == "true"
            }
        }
        assertScreenFits()
        rule.runOnIdle { output.value += "\u001b[2J\u001b[H\u001b[32mC:\\> redraw\u001b[0m\u001b[6n" }
        rule.waitUntil(5000) { screen().contains("redraw") }
        assertFalse(screen().contains("Microsoft Windows"))
        rule.waitUntil(5000) { sent.any { it.startsWith("\u001b[") && it.endsWith("R") } }
        rule.waitUntil(5000) { sizes.isNotEmpty() }
        val before = sizes.last()
        rule.runOnIdle { height.value = 160.dp }
        rule.waitUntil(5000) { sizes.last().second < before.second }
        assertScreenFits()
        assertTrue(screen().contains("redraw"))
        rule.runOnIdle { height.value = 300.dp }
        rule.waitUntil(5000) { sizes.last() == before }
        assertScreenFits()
        assertTrue(screen().contains("redraw"))
    }
}
