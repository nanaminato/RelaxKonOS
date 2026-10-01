package app.relaxkonos.mobile.ui.terminal

import java.nio.ByteBuffer
import java.nio.CharBuffer
import java.nio.charset.CodingErrorAction

/** Bounded VT text screen. Cursor addresses refer to the viewport, never to scrollback. */
internal class TerminalTranscript(private val maximumLines: Int = 800, private val retainRawOutput: Boolean = false) {
    var rawOutput: String = ""
        private set
    private val decoder = Charsets.UTF_8.newDecoder()
        .onMalformedInput(CodingErrorAction.REPLACE).onUnmappableCharacter(CodingErrorAction.REPLACE)
    private var pending = byteArrayOf()
    private val history = ArrayDeque<String>()
    private var screenRows = 24
    private var screenColumns = 80
    private val screen = MutableList(screenRows) { StringBuilder() }
    private var row = 0
    private var column = 0
    private var savedRow = 0
    private var savedColumn = 0
    private var scrollTop = 0
    private var scrollBottom = screenRows - 1
    private var wrapPending = false
    private var autoWrap = true
    private var state = State.Text
    private val sequence = StringBuilder()

    fun resize(columns: Int, rows: Int): String {
        val nextColumns = columns.coerceIn(20, 300)
        val nextRows = rows.coerceIn(5, 100)
        if (nextColumns == screenColumns && nextRows == screenRows) return snapshot()
        // Remove unused bottom rows first. Only rows displaced above the cursor become history.
        val displaced = (row - nextRows + 1).coerceAtLeast(0)
        repeat(displaced) { remember(screen.removeAt(0)) }
        row -= displaced
        savedRow = (savedRow - displaced).coerceIn(0, nextRows - 1)
        while (screen.size > nextRows) screen.removeAt(screen.lastIndex)
        while (screen.size < nextRows) screen.add(StringBuilder())
        screen.forEach { if (it.length > nextColumns) it.setLength(nextColumns) }
        if (wrapPending && nextColumns > screenColumns) column++
        screenColumns = nextColumns
        screenRows = nextRows
        column = column.coerceAtMost(screenColumns - 1)
        savedColumn = savedColumn.coerceAtMost(screenColumns - 1)
        scrollTop = 0
        scrollBottom = screenRows - 1
        wrapPending = false
        return snapshot()
    }

    fun append(bytes: ByteArray): String {
        val input = ByteBuffer.wrap(pending + bytes)
        val characters = CharBuffer.allocate(input.remaining() + 4)
        decoder.decode(input, characters, false)
        pending = ByteArray(input.remaining()).also { input.get(it) }
        characters.flip()
        if (retainRawOutput) rawOutput += characters.toString()
        while (characters.hasRemaining()) consume(characters.get())
        return snapshot()
    }

    private fun consume(ch: Char) {
        when (state) {
            State.Text -> when (ch) {
                '\u001b' -> state = State.Escape
                '\r' -> { column = 0; wrapPending = false }
                '\n', '\u000b', '\u000c' -> index()
                '\b' -> { column = (column - 1).coerceAtLeast(0); wrapPending = false }
                '\t' -> { column = (((column / 8) + 1) * 8).coerceAtMost(screenColumns - 1); wrapPending = false }
                else -> if (ch >= ' ' && ch != '\u007f') write(ch)
            }
            State.Escape -> {
                state = State.Text
                when (ch) {
                    '[' -> { sequence.clear(); state = State.Csi }
                    ']' -> state = State.Osc
                    'P', '^', '_' -> state = State.ControlString
                    '(', ')', '*', '+' -> state = State.Charset
                    '7' -> saveCursor()
                    '8' -> restoreCursor()
                    'D' -> index()
                    'E' -> { column = 0; index() }
                    'M' -> reverseIndex()
                    'c' -> reset()
                }
            }
            State.Csi -> when {
                ch == '\u001b' -> state = State.Escape
                ch in '@'..'~' -> { applyCsi(ch, sequence.toString()); state = State.Text }
                sequence.length < 64 -> sequence.append(ch)
            }
            State.Osc -> state = when (ch) { '\u0007' -> State.Text; '\u001b' -> State.StringEscape; else -> State.Osc }
            State.ControlString -> if (ch == '\u001b') state = State.StringEscape
            State.StringEscape -> state = if (ch == '\\') State.Text else State.ControlString
            State.Charset -> state = State.Text
        }
    }

    private fun write(ch: Char) {
        if (wrapPending) { column = 0; index() }
        val line = screen[row]
        while (line.length <= column) line.append(' ')
        line.setCharAt(column, ch)
        if (column == screenColumns - 1) wrapPending = autoWrap else column++
    }

    private fun index() {
        wrapPending = false
        if (row == scrollBottom) scrollUp(1) else row = (row + 1).coerceAtMost(screenRows - 1)
    }

    private fun reverseIndex() {
        wrapPending = false
        if (row == scrollTop) scrollDown(1) else row = (row - 1).coerceAtLeast(0)
    }

    private fun remember(line: StringBuilder) {
        history.addLast(line.toString().trimEnd())
        while (history.size > maximumLines) history.removeFirst()
    }

    private fun scrollUp(count: Int) {
        repeat(count.coerceAtMost(scrollBottom - scrollTop + 1)) {
            val removed = screen.removeAt(scrollTop)
            if (scrollTop == 0 && scrollBottom == screenRows - 1) remember(removed)
            screen.add(scrollBottom, StringBuilder())
        }
    }

    private fun scrollDown(count: Int) {
        repeat(count.coerceAtMost(scrollBottom - scrollTop + 1)) {
            screen.removeAt(scrollBottom)
            screen.add(scrollTop, StringBuilder())
        }
    }

    private fun saveCursor() { savedRow = row; savedColumn = column }
    private fun restoreCursor() { row = savedRow; column = savedColumn; wrapPending = false }

    private fun reset() {
        history.clear()
        screen.forEach { it.clear() }
        row = 0
        column = 0
        savedRow = 0
        savedColumn = 0
        scrollTop = 0
        scrollBottom = screenRows - 1
        wrapPending = false
        autoWrap = true
    }

    private fun applyCsi(command: Char, raw: String) {
        val privateMode = raw.startsWith('?')
        val numbers = raw.removePrefix("?").split(';').map { it.toIntOrNull() ?: 0 }
        fun count(index: Int = 0) = (numbers.getOrNull(index) ?: 0).coerceAtLeast(1)
        if (privateMode) {
            if ((command == 'h' || command == 'l') && 7 in numbers) {
                autoWrap = command == 'h'
                wrapPending = false
            }
            return
        }
        when (command) {
            'A' -> row = (row - count()).coerceAtLeast(0)
            'B', 'e' -> row = (row.toLong() + count()).coerceAtMost((screenRows - 1).toLong()).toInt()
            'C', 'a' -> column = (column.toLong() + count()).coerceAtMost((screenColumns - 1).toLong()).toInt()
            'D' -> column = (column - count()).coerceAtLeast(0)
            'E' -> { row = (row.toLong() + count()).coerceAtMost((screenRows - 1).toLong()).toInt(); column = 0 }
            'F' -> { row = (row - count()).coerceAtLeast(0); column = 0 }
            'G', '\u0060' -> column = (count() - 1).coerceAtMost(screenColumns - 1)
            'd' -> row = (count() - 1).coerceAtMost(screenRows - 1)
            'H', 'f' -> {
                row = (count() - 1).coerceAtMost(screenRows - 1)
                column = (count(1) - 1).coerceAtMost(screenColumns - 1)
            }
            'J' -> eraseDisplay(numbers.first())
            'K' -> eraseLine(screen[row], numbers.first())
            'S' -> scrollUp(count())
            'T' -> scrollDown(count())
            'L' -> if (row in scrollTop..scrollBottom) repeat(count().coerceAtMost(scrollBottom - row + 1)) {
                screen.removeAt(scrollBottom); screen.add(row, StringBuilder())
            }
            'M' -> if (row in scrollTop..scrollBottom) repeat(count().coerceAtMost(scrollBottom - row + 1)) {
                screen.removeAt(row); screen.add(scrollBottom, StringBuilder())
            }
            '@' -> {
                val line = screen[row]
                if (column < line.length) {
                    repeat(count().coerceAtMost(screenColumns - column)) { line.insert(column, ' ') }
                    if (line.length > screenColumns) line.setLength(screenColumns)
                }
            }
            'P' -> {
                val line = screen[row]
                if (column < line.length) line.delete(column, (column.toLong() + count()).coerceAtMost(line.length.toLong()).toInt())
            }
            'X' -> eraseCells(screen[row], column, (column.toLong() + count()).coerceAtMost(screenColumns.toLong()).toInt())
            'r' -> {
                val top = count() - 1
                val bottom = (numbers.getOrNull(1)?.takeIf { it > 0 } ?: screenRows) - 1
                if (top < bottom && bottom < screenRows) {
                    scrollTop = top; scrollBottom = bottom; row = 0; column = 0
                }
            }
            's' -> saveCursor()
            'u' -> restoreCursor()
        }
        // Presentation-only sequences must not cancel a pending wrap at the right margin.
        if (command in "ABCDEFGH\u0060adefJKSTLM@PXrsu") wrapPending = false
    }

    private fun eraseDisplay(mode: Int) {
        when (mode) {
            0 -> { eraseLine(screen[row], 0); for (index in row + 1 until screenRows) screen[index].clear() }
            1 -> { for (index in 0 until row) screen[index].clear(); eraseLine(screen[row], 1) }
            2 -> screen.forEach { it.clear() }
            3 -> history.clear()
        }
    }

    private fun eraseLine(line: StringBuilder, mode: Int) {
        when (mode) {
            0 -> if (column < line.length) line.setLength(column)
            1 -> eraseCells(line, 0, column + 1)
            2 -> line.clear()
        }
    }

    private fun eraseCells(line: StringBuilder, start: Int, end: Int) {
        for (index in start until end.coerceAtMost(line.length)) line.setCharAt(index, ' ')
    }

    private fun snapshot(): String {
        // Cursor positioning/erase/resize can touch blank rows without producing output history.
        val lastContentRow = screen.indexOfLast { it.any { character -> character != ' ' } }
        return (history.toList() + screen.take(lastContentRow + 1).map { it.toString().trimEnd() })
            .takeLast(maximumLines).joinToString("\n").takeLast(80_000)
    }

    private enum class State { Text, Escape, Csi, Osc, ControlString, StringEscape, Charset }
}
