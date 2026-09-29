package app.relaxkonos.mobile.ui.terminal

import org.junit.Assert.assertEquals
import org.junit.Test

class TerminalTranscriptTest {
    @Test fun `renders UTF-8 and terminal rewrites across transport chunks`() {
        val transcript = TerminalTranscript()
        val character = "中".toByteArray(Charsets.UTF_8)

        transcript.append(character.copyOfRange(0, 2))
        assertEquals("中", transcript.append(character.copyOfRange(2, character.size)))
        assertEquals("bye", transcript.append("\u001b[2Jhello\rbye\u001b[K".toByteArray()))
    }

    @Test fun `clears the screen and bounds retained terminal lines`() {
        val transcript = TerminalTranscript(maximumLines = 2)

        assertEquals("two\nthree", transcript.append("one\ntwo\nthree".toByteArray()))
        assertEquals("", transcript.append("\u001b[2J".toByteArray()))
    }
}
