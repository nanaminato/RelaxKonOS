package app.relaxkonos.mobile.ui.terminal

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import org.junit.Assert.*
import org.junit.Test

class TerminalPresentationTest {
    private fun owner() = SessionState.Active("host", "https://host", "alice", "work", emptySet(), "linux", ExecutionEligibility.Available,
        workspaceId = "11111111-1111-1111-1111-111111111111")
    @Test fun `session switching keeps distinct drafts and invalidates pending paste and modifiers`() {
        val p = TerminalPresentation(); p.bindOwner(owner()); p.bindSession("a"); p.edit("echo a")
        p.preparePaste("one\ntwo"); p.ctrlNext = true
        p.bindSession("b"); assertEquals("", p.input); assertNull(p.pasteReview); assertFalse(p.ctrlNext)
        p.edit("echo b"); p.bindSession("a"); assertEquals("echo a", p.input)
        p.bindSession("a"); assertEquals("echo a", p.input)
        assertTrue(p.preparePaste("echo safe\r")); assertTrue(p.canPaste("a")); assertFalse(p.canPaste("b"))
    }
    @Test fun `owner replacement even same account clears drafts output search and settings`() {
        val p = TerminalPresentation(); val first = owner(); p.bindOwner(first); p.bindSession("a"); p.edit("secret")
        p.search = "secret"; p.searchOpen = true; p.localFontSize = 20.0; p.preparePaste("secret")
        p.bindOwner(owner()); p.bindSession("a"); assertEquals("", p.input); assertNull(p.pasteReview)
        assertEquals("", p.search); assertNull(p.localFontSize); assertFalse(p.settingsVerified)
    }
    @Test fun `draft and clipboard bounds do not silently send truncated clipboard data`() {
        val p = TerminalPresentation(); p.bindOwner(owner()); p.bindSession("a")
        p.edit("a".repeat(20000)); assertEquals(16384, p.input.length)
        assertFalse(p.preparePaste("a".repeat(16385))); assertNull(p.pasteReview)
        assertTrue(p.preparePaste("x\r")); p.sent(); assertEquals("", p.input); assertNull(p.pasteReview)
    }
    @Test fun `clipboard send preserves an existing draft while a confirmed draft send clears it`() {
        val p = TerminalPresentation(); p.bindOwner(owner()); p.bindSession("a"); p.edit("unsent draft")
        p.preparePaste("clipboard"); p.acceptedPaste(); assertEquals("unsent draft", p.input)
        p.edit("\n" + "x".repeat(16383)); assertTrue(p.prepareDraftReview()); assertEquals(16385, p.pasteReview!!.payload.length)
        p.acceptedPaste(); assertEquals("", p.input)
    }
    @Test fun `control and alt input preserve shell keys without mapping unicode or multiline to controls`() {
        assertEquals("echo\r", TerminalInputPolicy.payload("echo", false, false))
        assertEquals("\u0003", TerminalInputPolicy.payload("c", true, false))
        assertEquals("\u001b\u0003", TerminalInputPolicy.payload("C", true, true))
        assertEquals("\u0000", TerminalInputPolicy.payload(" ", true, false))
        assertEquals("\u007f", TerminalInputPolicy.payload("?", true, false))
        assertEquals("界", TerminalInputPolicy.payload("界", true, false))
        assertTrue(TerminalInputPolicy.needsReview("a\rb")); assertTrue(TerminalInputPolicy.needsReview("a\u001bb"))
        assertTrue(TerminalInputPolicy.needsReview("a\nb")); assertFalse(TerminalInputPolicy.needsReview("echo"))
    }
    @Test fun `search is literal case insensitive bounded and reports utf16 positions for unicode`() {
        assertEquals(listOf(TerminalMatch(2, 4), TerminalMatch(5, 7)), TerminalInputPolicy.matches("😀Ab AB", "ab"))
        assertEquals(5000, TerminalInputPolicy.matches("x".repeat(80000), "x").size)
        assertTrue(TerminalInputPolicy.matches("text", "").isEmpty())
        assertEquals(listOf(TerminalMatch(1, 4)), TerminalInputPolicy.matches("x[a]", "[a]"))
    }
    @Test fun `tablet sidebar adapts to content width height and font scale`() {
        assertFalse(terminalTwoPane(839f, 600f, 1f)); assertTrue(terminalTwoPane(840f, 200f, 1f))
        assertFalse(terminalTwoPane(960f, 199f, 1f)); assertFalse(terminalTwoPane(960f, 300f, 2f))
        assertTrue(terminalTwoPane(960f, 400f, 2f))
    }

    @Test fun `special keys honor xterm modifiers and application cursor mode`() {
        assertEquals("\u001bOA", terminalKeyPayload("\u001b[A", applicationCursor = true))
        assertEquals("\u001b[1;5D", terminalKeyPayload("\u001b[D", ctrl = true, applicationCursor = true))
        assertEquals("\u001b[1;8H", terminalKeyPayload("\u001b[H", ctrl = true, alt = true, shift = true))
        assertEquals("\u001b[3;3~", terminalKeyPayload("\u001b[3~", alt = true))
        assertEquals("\u001b[1;2P", terminalKeyPayload("\u001bOP", shift = true))
        assertEquals("\u001b[Z", terminalKeyPayload("\t", shift = true))
        assertEquals("\u001b\b", terminalKeyPayload("\u007f", ctrl = true, alt = true))
        assertEquals("\u001b\u0003", terminalKeyPayload("\u0003", alt = true))
    }

    @Test fun `text bounds never split a supplementary Unicode character`() {
        assertEquals("ab", TerminalInputPolicy.boundedText("ab😀c", 3))
        assertEquals("ab😀", TerminalInputPolicy.boundedText("ab😀c", 4))
    }
}
