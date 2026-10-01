package app.relaxkonos.mobile.ui.terminal

import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue

class TerminalTranscriptTest {
    @Test fun `raw output preserves VT and UTF-8 split across frames for renderer switching`() {
        val transcript = TerminalTranscript(retainRawOutput = true)
        val bytes = "中".toByteArray(Charsets.UTF_8)
        transcript.append(bytes.copyOfRange(0, 2))
        assertEquals("", transcript.rawOutput)
        transcript.append(bytes.copyOfRange(2, bytes.size))
        transcript.append("\u001b[".toByteArray())
        assertEquals("中\u001b[", transcript.rawOutput)
        assertEquals("new", transcript.append("2J\u001b[Hnew".toByteArray()))
        assertEquals("中\u001b[2J\u001b[Hnew", transcript.rawOutput)
        transcript.resize(40, 12)
        assertEquals("中\u001b[2J\u001b[Hnew", transcript.rawOutput)
    }

    @Test fun `renders UTF-8 and terminal rewrites across transport chunks`() {
        val transcript = TerminalTranscript()
        val character = "中".toByteArray(Charsets.UTF_8)

        transcript.append(character.copyOfRange(0, 2))
        assertEquals("中", transcript.append(character.copyOfRange(2, character.size)))
        assertEquals("bye", transcript.append("\u001b[2J\u001b[Hhello\rbye\u001b[K".toByteArray()))
    }

    @Test fun `clears the screen and bounds retained terminal lines`() {
        val transcript = TerminalTranscript(maximumLines = 2)

        assertEquals("two\nthree", transcript.append("one\r\ntwo\r\nthree".toByteArray()))
        assertEquals("", transcript.append("\u001b[2J".toByteArray()))
    }

    @Test fun `Windows erase display removes stale prompts and blank rows without moving cursor`() {
        val transcript = TerminalTranscript()
        transcript.resize(48, 18)
        transcript.append("PS old>\r\nPS old>\u001b[18;1HPS old>".toByteArray())
        assertEquals("PS test>", transcript.append("\u001b[H\u001b[JPS test>".toByteArray()))
        assertEquals("    x", transcript.append("\u001b[1;5H\u001b[2Jx".toByteArray()))
    }

    @Test fun `absolute cursor addresses remain relative to screen after scrolling`() {
        val transcript = TerminalTranscript()
        transcript.resize(48, 5)
        transcript.append("one\r\ntwo\r\nthree\r\nfour\r\nfive\r\nsix".toByteArray())
        assertEquals("one\ntwo\nthree\nfour\nfive\nsix", transcript.append(byteArrayOf()))
        assertEquals("one\nnew\nthree\nfour\nfive\nsix", transcript.append("\u001b[Hnew\u001b[K".toByteArray()))
        assertEquals("one\nnew", transcript.append("\u001b[J".toByteArray()))
        assertEquals("one\nredraw", transcript.append("\u001b[Hredraw\u001b[K".toByteArray()))
    }

    @Test fun `supports vertical positioning and saved cursor across chunks`() {
        val transcript = TerminalTranscript()
        transcript.resize(48, 5)
        transcript.append("first\u001b7\u001b[5".toByteArray())
        assertEquals("first!\n\n\n\n     bottom", transcript.append("dbottom\u001b8!".toByteArray()))
    }

    @Test fun `erase line to cursor includes the cursor cell`() {
        val transcript = TerminalTranscript()
        assertEquals("   def", transcript.append("abcdef\u001b[3G\u001b[1K".toByteArray()))
    }

    @Test fun `line feed preserves column and wrapping waits for printable text`() {
        val transcript = TerminalTranscript()
        transcript.resize(20, 5)
        assertEquals("abc\n   d", transcript.append("abc\nd".toByteArray()))
        assertEquals("12345678901234567890\n!", transcript.append("\u001b[2J\u001b[H12345678901234567890\u001b[0m!".toByteArray()))
        assertEquals("!2345678901234567890", transcript.append("\u001b[2J\u001b[H12345678901234567890\r!".toByteArray()))
    }

    @Test fun `resize discards unused bottom rows before scrolling content into history`() {
        val transcript = TerminalTranscript()
        transcript.resize(48, 18)
        transcript.append("PS test>".toByteArray())
        assertEquals("PS test>", transcript.resize(48, 5))
        assertEquals("PS test>", transcript.resize(48, 18))
        assertEquals("PS test>", transcript.append("\u001b[HPS test>\u001b[J".toByteArray()))
    }

    @Test fun `resize preserves content and new origin when cursor is below smaller screen`() {
        val transcript = TerminalTranscript()
        transcript.resize(48, 8)
        transcript.append("one\r\ntwo\r\nthree\r\nfour\r\nfive\r\nsix\r\nseven\r\neight".toByteArray())
        assertEquals("one\ntwo\nthree\nfour\nfive\nsix\nseven\neight", transcript.resize(48, 5))
        assertEquals("one\ntwo\nthree\nnew\nfive\nsix\nseven\neight",
            transcript.append("\u001b[Hnew\u001b[K".toByteArray()))
    }

    @Test fun `scroll region and reverse index do not pollute history`() {
        val transcript = TerminalTranscript()
        transcript.resize(48, 5)
        transcript.append("one\r\ntwo\r\nthree\r\nfour\r\nfive".toByteArray())
        assertEquals("one\nthree\nfour\n\nfive", transcript.append("\u001b[2;4r\u001b[4;1H\n".toByteArray()))
        assertEquals("one\n\nthree\nfour\nfive", transcript.append("\u001b[2;1H\u001bM".toByteArray()))
    }

    @Test fun `clear scrollback leaves active screen and cursor untouched`() {
        val transcript = TerminalTranscript()
        transcript.resize(48, 5)
        transcript.append("history\r\none\r\ntwo\r\nthree\r\nfour\r\nfive".toByteArray())
        assertEquals("one\ntwo\nthree\nfour\nfive!", transcript.append("\u001b[3J!".toByteArray()))
    }

    @Test fun `Windows blank viewport repaint does not create output rows`() {
        val transcript = TerminalTranscript()
        transcript.resize(48, 7)
        assertEquals("", transcript.append("\u001b[?25l\u001b[2J\u001b[m\u001b[H\r\n\r\n\r\n\r\n\r\n\r\n\u001b[H\u001b[?25h".toByteArray()))
        transcript.resize(48, 18)
        assertEquals("", transcript.append(("\u001b[?25l" + "\u001b[K\r\n".repeat(17) + "\u001b[K\u001b[H\u001b[?25h").toByteArray()))
        assertEquals("PS test>", transcript.append("PS test>".toByteArray()))
    }

    @Test fun `growing columns after right margin preserves the last character`() {
        val transcript = TerminalTranscript()
        transcript.resize(20, 5)
        transcript.append("12345678901234567890".toByteArray())
        transcript.resize(30, 5)
        assertEquals("12345678901234567890!", transcript.append("!".toByteArray()))
    }

    @Test fun `erase above cursor preserves subsequent cells and screen rows`() {
        val transcript = TerminalTranscript()
        transcript.append("one\r\nabcdef\r\nlast".toByteArray())
        assertEquals("\n   def\nlast", transcript.append("\u001b[2;3H\u001b[1J".toByteArray()))
    }

    @Test fun `insert delete and erase characters redraw a prompt in place`() {
        val transcript = TerminalTranscript()
        transcript.append("PS test> abc".toByteArray())
        assertEquals("PS test> a bc", transcript.append("\u001b[11G\u001b[@".toByteArray()))
        assertEquals("PS test> abc", transcript.append("\u001b[P".toByteArray()))
        assertEquals("PS test> a c", transcript.append("\u001b[X".toByteArray()))
    }

    @Test fun `scroll commands and line insertion stay inside the active region`() {
        val transcript = TerminalTranscript()
        transcript.resize(48, 5)
        transcript.append("one\r\ntwo\r\nthree\r\nfour\r\nfive\u001b[2;4r\u001b[2;1H".toByteArray())
        assertEquals("one\n\ntwo\nthree\nfive", transcript.append("\u001b[L".toByteArray()))
        assertEquals("one\ntwo\nthree\n\nfive", transcript.append("\u001b[M".toByteArray()))
        assertEquals("one\nthree\n\n\nfive", transcript.append("\u001b[S".toByteArray()))
        assertEquals("one\n\nthree\n\nfive", transcript.append("\u001b[T".toByteArray()))
    }

    @Test fun `styles survive chunks scrolling cursor rewrites and reset`() {
        val transcript = TerminalTranscript()
        transcript.resize(20, 5)
        transcript.append("\u001b[1;3;4;31;48;2;10;20".toByteArray())
        assertEquals("red!", transcript.append(";30mred\u001b[0m!".toByteArray()))
        val run = transcript.frame.styles.single()
        assertEquals(0, run.start); assertEquals(3, run.end)
        assertEquals(TerminalTranscript.ansiColor(1), run.style.foreground)
        assertEquals(0x0a141e, run.style.background)
        assertTrue(run.style.bold && run.style.italic && run.style.underline)
        transcript.append("\r\n2\r\n3\r\n4\r\n5\r\n6".toByteArray())
        assertEquals(run, transcript.frame.styles.first())
        transcript.append("\u001b[H\u001b[38:2::100:150:200mnew".toByteArray())
        assertEquals(0x6496c8, transcript.frame.styles.last().style.foreground)
        transcript.append("\u001bc".toByteArray())
        assertTrue(transcript.frame.styles.isEmpty())
        assertEquals("", transcript.append(byteArrayOf()))
    }

    @Test fun `256 colors grayscale defaults and selective attributes have current cell styles`() {
        val transcript = TerminalTranscript()
        transcript.append("\u001b[38;5;196;48;5;244;7;8;9mA\u001b[27;28;29;39;49mB".toByteArray())
        val style = transcript.frame.styles.single().style
        assertEquals(0xff0000, style.foreground); assertEquals(0x808080, style.background)
        assertTrue(style.inverse && style.concealed && style.strike)
        assertEquals(1, transcript.frame.styles.single().end)
    }

    @Test fun `CJK emoji combining marks and wide continuation replacement use cell addresses`() {
        val transcript = TerminalTranscript()
        assertEquals("中a😀é!", transcript.append("中a😀e\u0301!".toByteArray()))
        assertEquals(7, transcript.frame.cursor) // UTF-16 index, seven display cells precede the cursor.
        assertEquals(" Xa😀é!", transcript.append("\u001b[2GX".toByteArray()))
        assertEquals(" X😀é!", transcript.append("\u001b[3G\u001b[P".toByteArray()))
        assertEquals(" X  é!", transcript.append("\u001b[4G\u001b[X".toByteArray()))
        assertEquals("🇯🇵👩‍💻Z", transcript.append("\u001b[2J\u001b[H🇯🇵👩‍💻Z".toByteArray()))
        assertEquals("🇯🇵!👩‍💻Z", transcript.append("\u001b[3G\u001b[@!".toByteArray()))
    }

    @Test fun `wide right margin wrap waits for a whole glyph and resize drops orphan cells`() {
        val transcript = TerminalTranscript()
        transcript.resize(20, 5)
        assertEquals("x".repeat(19) + "\n中!", transcript.append(("x".repeat(19) + "中!").toByteArray()))
        transcript.append("\u001b[2J\u001b[H".toByteArray())
        transcript.resize(21, 5)
        transcript.append(("a".repeat(19) + "中").toByteArray())
        assertEquals("a".repeat(19), transcript.resize(20, 5))
        assertEquals("a".repeat(19) + "!", transcript.append("!".toByteArray()))
    }

    @Test fun `alternate screen keeps primary history cursor and attributes out of full screen apps`() {
        val transcript = TerminalTranscript()
        transcript.resize(20, 5)
        transcript.append("history\r\n1\r\n2\r\n3\r\n4\r\n\u001b[32mprompt".toByteArray())
        assertEquals("界面", transcript.append("\u001b[?1049h\u001b[H界面".toByteArray()))
        transcript.append("\r\n1\r\n2\r\n3\r\n4\r\n5".toByteArray())
        assertTrue(!transcript.frame.text.contains("history"))
        assertEquals("history\n1\n2\n3\n4\nprompt!", transcript.append("\u001b[?1049l!".toByteArray()))
        assertEquals(TerminalTranscript.ansiColor(2), transcript.frame.styles.last().style.foreground)
    }

    @Test fun `cursor visibility application keys paste mode origin and DEC graphics are rendered facts`() {
        val transcript = TerminalTranscript()
        transcript.append("\u001b[?1h\u001b[?2004h\u001b[?25l".toByteArray())
        assertNull(transcript.frame.cursor)
        assertTrue(transcript.frame.applicationCursor && transcript.frame.bracketedPaste)
        transcript.resize(20, 5)
        assertEquals("\n┌─┐", transcript.append("\u001b[2;4r\u001b[?6h\u001b[H\u001b(0lqk\u001b(B\u001b[?25h".toByteArray()))
        assertEquals(4, transcript.frame.cursor)
        assertEquals("\n┌─┐!", transcript.append("\u001b[4h!".toByteArray()))
    }

    @Test fun `device query replies reflect cell cursor and are bounded without treating unknown controls as text`() {
        val transcript = TerminalTranscript()
        transcript.append("中\u001b[5n\u001b[6n\u001b[c".toByteArray())
        assertEquals(listOf("\u001b[0n", "\u001b[1;3R", "\u001b[?1;2c"), transcript.drainResponses())
        assertTrue(transcript.drainResponses().isEmpty())
        transcript.append("\u001b[6n".repeat(100).toByteArray())
        assertEquals(16, transcript.drainResponses().size)
        assertEquals("中", transcript.append("\u001b]52;clipboard;ignored\u0007\u001bPignored\u001b\\".toByteArray()))
    }

    @Test fun `rendered glyph facts preserve fixed grid widths and bound combining sequences`() {
        val transcript = TerminalTranscript()
        transcript.append("中a😀e\u0301".toByteArray())
        assertEquals(listOf(2, 2, 1), transcript.frame.glyphs.map { it.width })
        assertEquals(listOf("中", "😀", "é"), transcript.frame.glyphs.map { transcript.frame.text.substring(it.start, it.end) })
        transcript.append("\u0301".repeat(100_000).toByteArray())
        assertTrue(transcript.frame.glyphs.last().end - transcript.frame.glyphs.last().start <= 64)
        assertTrue(transcript.frame.text.length < 100)
    }
}
