package app.relaxkonos.mobile.ui.terminal

import java.nio.ByteBuffer
import java.nio.CharBuffer
import java.nio.charset.CodingErrorAction

/** Bounded mobile VT screen for interactive diagnostics. No control bytes are rendered as text. */
internal class TerminalTranscript(private val maximumLines: Int = 800) {
    private val decoder = Charsets.UTF_8.newDecoder()
        .onMalformedInput(CodingErrorAction.REPLACE).onUnmappableCharacter(CodingErrorAction.REPLACE)
    private var pending = byteArrayOf()
    private val lines = mutableListOf(StringBuilder())
    private var row = 0
    private var column = 0
    private var screenRows = 24
    private var screenColumns = 80
    private var state = State.Text
    private val sequence = StringBuilder()

    fun resize(columns: Int, rows: Int) {
        screenColumns = columns.coerceIn(20, 300)
        screenRows = rows.coerceIn(5, 100)
    }

    fun append(bytes: ByteArray): String {
        val input = ByteBuffer.wrap(pending + bytes)
        val characters = CharBuffer.allocate(input.remaining() + 4)
        decoder.decode(input, characters, false)
        pending = ByteArray(input.remaining()).also { input.get(it) }
        characters.flip()
        while (characters.hasRemaining()) consume(characters.get())
        return snapshot()
    }

    private fun consume(ch: Char) {
        when (state) {
            State.Text -> when (ch) {
                '\u001b' -> state = State.Escape
                '\r' -> column = 0
                '\n' -> newline()
                '\b' -> column = (column - 1).coerceAtLeast(0)
                '\t' -> column = ((column / 8) + 1) * 8
                else -> if (ch >= ' ') write(ch)
            }
            State.Escape -> state = when (ch) {
                '[' -> { sequence.clear(); State.Csi }
                ']' -> State.Osc
                else -> State.Text
            }
            State.Csi -> if (ch in '@'..'~') {
                applyCsi(ch, sequence.toString())
                state = State.Text
            } else if (sequence.length < 32) sequence.append(ch)
            State.Osc -> state = when (ch) { '\u0007' -> State.Text; '\u001b' -> State.OscEscape; else -> State.Osc }
            State.OscEscape -> state = if (ch == '\\') State.Text else State.Osc
        }
    }

    private fun write(ch: Char) {
        if (column >= screenColumns) newline()
        ensureRow()
        val line = lines[row]
        while (line.length < column) line.append(' ')
        if (column == line.length) line.append(ch) else line.setCharAt(column, ch)
        column++
    }

    private fun newline() {
        row++
        column = 0
        ensureRow()
    }

    private fun ensureRow() {
        while (lines.size <= row) lines.add(StringBuilder())
        while (lines.size > maximumLines) {
            lines.removeAt(0)
            row = (row - 1).coerceAtLeast(0)
        }
    }

    private fun applyCsi(command: Char, raw: String) {
        val numbers = raw.trimStart('?').split(';').map { it.toIntOrNull() ?: 0 }
        fun count() = (numbers.firstOrNull() ?: 0).coerceAtLeast(1)
        val top = (lines.size - screenRows).coerceAtLeast(0)
        when (command) {
            'A' -> row = (row - count()).coerceAtLeast(top)
            'B' -> { row += count(); ensureRow() }
            'C' -> column = (column + count()).coerceAtMost(screenColumns - 1)
            'D' -> column = (column - count()).coerceAtLeast(0)
            'G' -> column = (count() - 1).coerceIn(0, screenColumns - 1)
            'H', 'f' -> {
                row = top + (count() - 1).coerceIn(0, screenRows - 1)
                column = ((numbers.getOrNull(1) ?: 1).coerceAtLeast(1) - 1).coerceIn(0, screenColumns - 1)
                ensureRow()
            }
            'J' -> if (numbers.firstOrNull() == 2) { lines.clear(); lines.add(StringBuilder()); row = 0; column = 0 }
            'K' -> {
                ensureRow()
                val line = lines[row]
                when (numbers.firstOrNull() ?: 0) {
                    0 -> if (column < line.length) line.delete(column, line.length)
                    1 -> { val end = column.coerceAtMost(line.length); for (index in 0 until end) line.setCharAt(index, ' ') }
                    2 -> line.clear()
                }
            }
            // SGR and private modes affect presentation; unsupported ones are safely ignored.
        }
    }

    private fun snapshot(): String = lines.joinToString("\n") { it.toString().trimEnd() }.takeLast(80_000)
    private enum class State { Text, Escape, Csi, Osc, OscEscape }
}
