package app.relaxkonos.mobile.ui.terminal

import java.nio.ByteBuffer
import java.nio.CharBuffer
import java.nio.charset.CodingErrorAction

data class TerminalCellStyle(
    val foreground: Int? = null, val background: Int? = null,
    val bold: Boolean = false, val faint: Boolean = false, val italic: Boolean = false,
    val underline: Boolean = false, val strike: Boolean = false,
    val inverse: Boolean = false, val concealed: Boolean = false,
)
data class TerminalGlyphRun(val start: Int, val end: Int, val width: Int, val style: TerminalCellStyle)
data class TerminalStyleRun(val start: Int, val end: Int, val style: TerminalCellStyle)
data class TerminalRenderFrame(
    val text: String = "", val styles: List<TerminalStyleRun> = emptyList(),
    val cursor: Int? = null, val cursorWidth: Int = 1,
    val applicationCursor: Boolean = false, val bracketedPaste: Boolean = false,
    val glyphs: List<TerminalGlyphRun> = emptyList(),
)

/** Bounded VT cell screen. Addresses and wide-character continuations belong to the viewport. */
internal class TerminalTranscript(private val maximumLines: Int = 800) {
    private data class Cell(val text: String = " ", val width: Int = 1, val style: TerminalCellStyle = TerminalCellStyle())
    private class Screen(rows: Int) {
        val lines = MutableList(rows) { mutableListOf<Cell>() }
        var row = 0; var column = 0
        var savedRow = 0; var savedColumn = 0; var savedStyle = TerminalCellStyle()
        var scrollTop = 0; var scrollBottom = rows - 1
        var wrapPending = false; var originMode = false
    }
    private val decoder = Charsets.UTF_8.newDecoder()
        .onMalformedInput(CodingErrorAction.REPLACE).onUnmappableCharacter(CodingErrorAction.REPLACE)
    private var pending = byteArrayOf()
    private val history = ArrayDeque<List<Cell>>()
    private var screenRows = 24; private var screenColumns = 80
    private var primary = Screen(screenRows); private var alternate = Screen(screenRows)
    private var alternateActive = false
    private val screen get() = if (alternateActive) alternate else primary
    private var style = TerminalCellStyle()
    private var autoWrap = true; private var insertMode = false; private var cursorVisible = true
    private var applicationCursor = false; private var bracketedPaste = false
    private var state = State.Text
    private val sequence = StringBuilder()
    private var highSurrogate: Char? = null
    private var graphicsCharset = false
    private val responses = ArrayDeque<String>()
    fun drainResponses(): List<String> = responses.toList().also { responses.clear() }
    private fun respond(value: String) { if (responses.size < 16) responses.addLast(value) }
    var frame = TerminalRenderFrame()
        private set

    fun resize(columns: Int, rows: Int): String {
        val nextColumns = columns.coerceIn(20, 300); val nextRows = rows.coerceIn(5, 100)
        if (nextColumns == screenColumns && nextRows == screenRows) return snapshot()
        listOf(primary, alternate).forEach { buffer ->
            val displaced = (buffer.row - nextRows + 1).coerceAtLeast(0)
            repeat(displaced) { val removed = buffer.lines.removeAt(0); if (buffer === primary) remember(removed) }
            buffer.row -= displaced
            buffer.savedRow = (buffer.savedRow - displaced).coerceIn(0, nextRows - 1)
            while (buffer.lines.size > nextRows) buffer.lines.removeAt(buffer.lines.lastIndex)
            while (buffer.lines.size < nextRows) buffer.lines.add(mutableListOf())
            buffer.lines.forEach { truncate(it, nextColumns) }
            if (buffer.wrapPending && nextColumns > screenColumns) buffer.column++
            buffer.column = buffer.column.coerceAtMost(nextColumns - 1)
            buffer.savedColumn = buffer.savedColumn.coerceAtMost(nextColumns - 1)
            buffer.scrollTop = 0; buffer.scrollBottom = nextRows - 1; buffer.wrapPending = false
        }
        screenColumns = nextColumns; screenRows = nextRows
        return snapshot()
    }

    fun clearLocal() { history.clear(); screen.lines.forEach { it.clear() }; snapshot() }

    fun append(bytes: ByteArray): String {
        val input = ByteBuffer.wrap(pending + bytes)
        val characters = CharBuffer.allocate(input.remaining() + 4)
        decoder.decode(input, characters, false)
        pending = ByteArray(input.remaining()).also { input.get(it) }
        characters.flip()
        while (characters.hasRemaining()) {
            val ch = characters.get()
            val high = highSurrogate
            highSurrogate = null
            if (high != null) {
                if (ch.isLowSurrogate()) { consume(Character.toCodePoint(high, ch)); continue }
                consume(0xfffd)
            }
            if (ch.isHighSurrogate()) highSurrogate = ch else consume(ch.code)
        }
        return snapshot()
    }

    private fun consume(code: Int) {
        val ch = code.toChar()
        when (state) {
            State.Text -> when (code) {
                27 -> state = State.Escape
                13 -> { screen.column = 0; screen.wrapPending = false }
                10, 11, 12 -> index()
                8 -> { screen.column = (screen.column - 1).coerceAtLeast(0); screen.wrapPending = false }
                9 -> { screen.column = (((screen.column / 8) + 1) * 8).coerceAtMost(screenColumns - 1); screen.wrapPending = false }
                else -> if (code >= 32 && code !in 127..159) write(code)
            }
            State.Escape -> {
                state = State.Text
                when (ch) {
                    '[' -> { sequence.clear(); state = State.Csi }
                    ']' -> state = State.Osc
                    'P', '^', '_' -> state = State.ControlString
                    '(' -> state = State.Charset
                    ')', '*', '+' -> state = State.IgnoredCharset
                    '7' -> saveCursor(); '8' -> restoreCursor()
                    'D' -> index(); 'E' -> { screen.column = 0; index() }; 'M' -> reverseIndex()
                    'c' -> reset()
                }
            }
            State.Csi -> when {
                ch == '\u001b' -> state = State.Escape
                ch in '@'..'~' -> { applyCsi(ch, sequence.toString()); state = State.Text }
                sequence.length < 256 -> sequence.append(ch)
            }
            State.Osc -> state = when (code) { 7 -> State.Text; 27 -> State.StringEscape; else -> State.Osc }
            State.ControlString -> if (code == 27) state = State.StringEscape
            State.StringEscape -> state = if (ch == '\\') State.Text else State.ControlString
            State.Charset -> { graphicsCharset = ch == '0'; state = State.Text }
            State.IgnoredCharset -> state = State.Text
        }
    }

    private fun write(code: Int) {
        val value = if (graphicsCharset && code in 0x60..0x7e) graphics[code - 0x60].toString() else String(Character.toChars(code))
        val width = terminalCellWidth(code)
        // Marks, variation selectors and joined emoji belong to the preceding cell, including at a margin.
        val previousColumn = if (screen.wrapPending) screen.column else screen.column - 1
        val lineBeforeWrap = screen.lines[screen.row]
        val previous = previousColumn.takeIf { it in lineBeforeWrap.indices }?.let { baseColumn(lineBeforeWrap, it) }
        if (previous != null) {
            val cell = lineBeforeWrap[previous]
            val previousCode = cell.text.codePointAt(0)
            val joined = cell.text.endsWith("\u200d") || (code in 0x1f1e6..0x1f1ff && previousCode in 0x1f1e6..0x1f1ff && cell.text.codePointCount(0, cell.text.length) == 1)
            if (width == 0 || joined) { if (cell.text.length + value.length <= 64) lineBeforeWrap[previous] = cell.copy(text = cell.text + value); return }
        }
        if (width == 0) return
        if (screen.wrapPending || (width == 2 && screen.column == screenColumns - 1 && autoWrap)) {
            screen.column = 0; index()
        }
        val actualWidth = if (width == 2 && screen.column == screenColumns - 1) 1 else width
        val line = screen.lines[screen.row]
        val start = screen.column
        if (insertMode && start < line.size) repeat(actualWidth) { line.add(start, blank()) }
        ensure(line, start + actualWidth)
        eraseGlyph(line, start)
        if (actualWidth == 2) eraseGlyph(line, start + 1)
        line[start] = Cell(value, actualWidth, style)
        if (actualWidth == 2) line[start + 1] = Cell("", 0, style)
        truncate(line, screenColumns)
        if (start + actualWidth >= screenColumns) { screen.column = screenColumns - 1; screen.wrapPending = autoWrap }
        else screen.column += actualWidth
    }

    private fun blank() = Cell(style = style)
    private fun ensure(line: MutableList<Cell>, length: Int) { while (line.size < length) line.add(blank()) }
    private fun baseColumn(line: List<Cell>, index: Int) = if (line[index].width == 0 && index > 0) index - 1 else index
    private fun eraseGlyph(line: MutableList<Cell>, index: Int) {
        if (index !in line.indices) return
        val base = baseColumn(line, index)
        val width = line[base].width
        repeat(width) { if (base + it in line.indices) line[base + it] = blank() }
    }
    private fun truncate(line: MutableList<Cell>, length: Int) {
        while (line.size > length) line.removeAt(line.lastIndex)
        if (line.lastOrNull()?.width == 2) line[line.lastIndex] = blank()
        for (index in line.indices) if (line[index].width == 0 && (index == 0 || line[index - 1].width != 2)) line[index] = blank()
    }
    private fun index() {
        screen.wrapPending = false
        if (screen.row == screen.scrollBottom) scrollUp(1) else screen.row = (screen.row + 1).coerceAtMost(screenRows - 1)
    }
    private fun reverseIndex() {
        screen.wrapPending = false
        if (screen.row == screen.scrollTop) scrollDown(1) else screen.row = (screen.row - 1).coerceAtLeast(0)
    }
    private fun remember(line: List<Cell>) {
        history.addLast(line.toList())
        while (history.size > maximumLines) history.removeFirst()
    }
    private fun scrollUp(count: Int) {
        repeat(count.coerceAtMost(screen.scrollBottom - screen.scrollTop + 1)) {
            val removed = screen.lines.removeAt(screen.scrollTop)
            if (!alternateActive && screen.scrollTop == 0 && screen.scrollBottom == screenRows - 1) remember(removed)
            screen.lines.add(screen.scrollBottom, mutableListOf())
        }
    }
    private fun scrollDown(count: Int) {
        repeat(count.coerceAtMost(screen.scrollBottom - screen.scrollTop + 1)) {
            screen.lines.removeAt(screen.scrollBottom); screen.lines.add(screen.scrollTop, mutableListOf())
        }
    }
    private fun saveCursor() { screen.savedRow = screen.row; screen.savedColumn = screen.column; screen.savedStyle = style }
    private fun restoreCursor() { screen.row = screen.savedRow; screen.column = screen.savedColumn; style = screen.savedStyle; screen.wrapPending = false }
    private fun reset() {
        history.clear(); primary = Screen(screenRows); alternate = Screen(screenRows); alternateActive = false
        responses.clear(); style = TerminalCellStyle(); autoWrap = true; insertMode = false; cursorVisible = true
        applicationCursor = false; bracketedPaste = false; graphicsCharset = false
    }

    private fun applyCsi(command: Char, raw: String) {
        val privateMode = raw.startsWith('?')
        val numbers = raw.removePrefix("?").split(';').map { it.toIntOrNull() ?: 0 }
        fun count(index: Int = 0) = (numbers.getOrNull(index) ?: 0).coerceAtLeast(1)
        if (command == 'h' || command == 'l') {
            val enabled = command == 'h'
            numbers.forEach { mode ->
                if (!privateMode) { if (mode == 4) insertMode = enabled }
                else when (mode) {
                    1 -> applicationCursor = enabled
                    6 -> { screen.originMode = enabled; screen.row = if (enabled) screen.scrollTop else 0; screen.column = 0; screen.wrapPending = false }
                    7 -> { autoWrap = enabled; screen.wrapPending = false }
                    25 -> cursorVisible = enabled
                    47, 1047, 1049 -> {
                        if (enabled && !alternateActive) {
                            if (mode == 1049) saveCursor()
                            if (mode != 47) alternate = Screen(screenRows)
                            alternateActive = true
                        } else if (!enabled && alternateActive) { alternateActive = false; if (mode == 1049) restoreCursor() }
                    }
                    1048 -> if (enabled) saveCursor() else restoreCursor()
                    2004 -> bracketedPaste = enabled
                }
            }
            return
        }
        if (command == 'n') {
            when (numbers.first()) {
                5 -> if (!privateMode) respond("\u001b[0n")
                6 -> respond("\u001b[${if (privateMode) "?" else ""}${screen.row - (if (screen.originMode) screen.scrollTop else 0) + 1};${screen.column + 1}R")
            }
            return
        }
        if (privateMode) return
        val rowMinimum = if (screen.originMode) screen.scrollTop else 0
        val rowMaximum = if (screen.originMode) screen.scrollBottom else screenRows - 1
        when (command) {
            'A' -> screen.row = (screen.row - count()).coerceAtLeast(rowMinimum)
            'B', 'e' -> screen.row = (screen.row.toLong() + count()).coerceAtMost(rowMaximum.toLong()).toInt()
            'C', 'a' -> screen.column = (screen.column.toLong() + count()).coerceAtMost((screenColumns - 1).toLong()).toInt()
            'D' -> screen.column = (screen.column - count()).coerceAtLeast(0)
            'E' -> { screen.row = (screen.row.toLong() + count()).coerceAtMost(rowMaximum.toLong()).toInt(); screen.column = 0 }
            'F' -> { screen.row = (screen.row - count()).coerceAtLeast(rowMinimum); screen.column = 0 }
            'G', '`' -> screen.column = (count() - 1).coerceAtMost(screenColumns - 1)
            'd' -> screen.row = (rowMinimum.toLong() + count() - 1).coerceAtMost(rowMaximum.toLong()).toInt()
            'H', 'f' -> {
                screen.row = (rowMinimum.toLong() + count() - 1).coerceAtMost(rowMaximum.toLong()).toInt()
                screen.column = (count(1) - 1).coerceAtMost(screenColumns - 1)
            }
            'J' -> eraseDisplay(numbers.first()); 'K' -> eraseLine(screen.lines[screen.row], numbers.first())
            'S' -> scrollUp(count()); 'T' -> scrollDown(count())
            'L' -> if (screen.row in screen.scrollTop..screen.scrollBottom) repeat(count().coerceAtMost(screen.scrollBottom - screen.row + 1)) {
                screen.lines.removeAt(screen.scrollBottom); screen.lines.add(screen.row, mutableListOf())
            }
            'M' -> if (screen.row in screen.scrollTop..screen.scrollBottom) repeat(count().coerceAtMost(screen.scrollBottom - screen.row + 1)) {
                screen.lines.removeAt(screen.row); screen.lines.add(screen.scrollBottom, mutableListOf())
            }
            '@' -> {
                val line = screen.lines[screen.row]
                if (screen.column < line.size) {
                    if (line[screen.column].width == 0) eraseGlyph(line, screen.column)
                    repeat(count().coerceAtMost(screenColumns - screen.column)) { line.add(screen.column, blank()) }
                    truncate(line, screenColumns)
                }
            }
            'P' -> {
                val line = screen.lines[screen.row]
                if (screen.column < line.size) {
                    val end = (screen.column.toLong() + count()).coerceAtMost(line.size.toLong()).toInt()
                    if (line[screen.column].width == 0) eraseGlyph(line, screen.column)
                    if (end < line.size && line[end].width == 0) eraseGlyph(line, end)
                    repeat(end - screen.column) { line.removeAt(screen.column) }; truncate(line, screenColumns)
                }
            }
            'X' -> eraseCells(screen.lines[screen.row], screen.column, (screen.column.toLong() + count()).coerceAtMost(screenColumns.toLong()).toInt())
            'r' -> {
                val top = count() - 1; val bottom = (numbers.getOrNull(1)?.takeIf { it > 0 } ?: screenRows) - 1
                if (top < bottom && bottom < screenRows) { screen.scrollTop = top; screen.scrollBottom = bottom; screen.row = if (screen.originMode) top else 0; screen.column = 0 }
            }
            's' -> saveCursor(); 'u' -> restoreCursor(); 'm' -> applyStyle(raw)
            'c' -> if (raw.isEmpty() || raw == "0") respond("\u001b[?1;2c")
        }
        if (command in "ABCDEFGH`adefJKSTLM@PXrsu") screen.wrapPending = false
    }

    private fun applyStyle(raw: String) {
        // xterm accepts semicolon extended colors and colon RGB with an optional color-space slot.
        val values = raw.split(';')
        var index = 0
        while (index < values.size) {
            val components = values[index++].split(':')
            val code = components.first().toIntOrNull() ?: 0
            when (code) {
                0 -> style = TerminalCellStyle()
                1 -> style = style.copy(bold = true); 2 -> style = style.copy(faint = true)
                3 -> style = style.copy(italic = true); 4, 21 -> style = style.copy(underline = true)
                7 -> style = style.copy(inverse = true); 8 -> style = style.copy(concealed = true); 9 -> style = style.copy(strike = true)
                22 -> style = style.copy(bold = false, faint = false); 23 -> style = style.copy(italic = false)
                24 -> style = style.copy(underline = false); 27 -> style = style.copy(inverse = false)
                28 -> style = style.copy(concealed = false); 29 -> style = style.copy(strike = false)
                in 30..37 -> style = style.copy(foreground = ansiColor(code - 30))
                in 40..47 -> style = style.copy(background = ansiColor(code - 40))
                in 90..97 -> style = style.copy(foreground = ansiColor(code - 90 + 8))
                in 100..107 -> style = style.copy(background = ansiColor(code - 100 + 8))
                39 -> style = style.copy(foreground = null); 49 -> style = style.copy(background = null)
                38, 48 -> {
                    val extended = if (components.size > 1) components.drop(1).map { it.toIntOrNull() }
                        else {
                            val mode = values.getOrNull(index)?.toIntOrNull(); val size = if (mode == 2) 4 else if (mode == 5) 2 else 1
                            val next = values.drop(index).take(size).map { it.toIntOrNull() }; index += next.size; next
                        }
                    val color = when (extended.firstOrNull()) {
                        5 -> extended.getOrNull(1)?.takeIf { it in 0..255 }?.let(::ansiColor)
                        2 -> extended.drop(if (extended.size == 5) 2 else 1).takeIf { it.size == 3 && it.all { value -> value != null && value in 0..255 } }
                            ?.let { (it[0]!! shl 16) or (it[1]!! shl 8) or it[2]!! }
                        else -> null
                    }
                    if (color != null) style = if (code == 38) style.copy(foreground = color) else style.copy(background = color)
                }
            }
        }
    }

    private fun eraseDisplay(mode: Int) {
        when (mode) {
            0 -> { eraseLine(screen.lines[screen.row], 0); for (index in screen.row + 1 until screenRows) clearLine(screen.lines[index]) }
            1 -> { for (index in 0 until screen.row) clearLine(screen.lines[index]); eraseLine(screen.lines[screen.row], 1) }
            2 -> screen.lines.forEach(::clearLine)
            3 -> history.clear()
        }
    }
    private fun clearLine(line: MutableList<Cell>) { line.clear(); if (style.background != null || style.inverse) ensure(line, screenColumns) }
    private fun eraseLine(line: MutableList<Cell>, mode: Int) {
        when (mode) {
            0 -> {
                if (screen.column < line.size && line[screen.column].width == 0) eraseGlyph(line, screen.column)
                while (line.size > screen.column) line.removeAt(line.lastIndex)
                if (style.background != null || style.inverse) ensure(line, screenColumns)
            }
            1 -> eraseCells(line, 0, screen.column + 1)
            2 -> clearLine(line)
        }
    }
    private fun eraseCells(line: MutableList<Cell>, start: Int, end: Int) {
        if (style.background != null || style.inverse) ensure(line, end)
        for (index in start until end.coerceAtMost(line.size)) eraseGlyph(line, index)
    }
    private fun contentEnd(line: List<Cell>): Int {
        val last = line.indexOfLast { it.text.isNotBlank() || it.style.background != null || it.style.inverse }
        return if (last < 0) 0 else (last + line[last].width).coerceAtMost(line.size)
    }
    private fun plainLine(line: List<Cell>) = line.take(contentEnd(line)).joinToString("") { it.text }.trimEnd()

    private fun snapshot(): String {
        val scrollback = if (alternateActive) emptyList() else history.toList()
        val lastContentRow = screen.lines.indexOfLast { contentEnd(it) > 0 }
        val plain = (scrollback + screen.lines.take(lastContentRow + 1)).takeLast(maximumLines)
            .joinToString("\n", transform = ::plainLine).takeLast(80_000)
        val lastRenderRow = if (cursorVisible) maxOf(lastContentRow, screen.row) else lastContentRow
        val lines = (scrollback + screen.lines.take(lastRenderRow + 1)).takeLast(maximumLines)
        val firstRetained = scrollback.size + lastRenderRow + 1 - lines.size
        val cursorRow = scrollback.size + screen.row - firstRetained
        val text = StringBuilder(); val runs = mutableListOf<TerminalStyleRun>(); val glyphs = mutableListOf<TerminalGlyphRun>()
        var cursor: Int? = null; var cursorWidth = 1
        lines.forEachIndexed { lineIndex, cells ->
            if (lineIndex > 0) text.append('\n')
            val cursorColumn = if (cursorVisible && lineIndex == cursorRow) {
                if (screen.column in cells.indices) baseColumn(cells, screen.column) else screen.column
            } else null
            val length = maxOf(contentEnd(cells), (cursorColumn ?: -1) + 1)
            for (index in 0 until length) {
                val cell = cells.getOrNull(index) ?: Cell()
                if (cell.width == 0) continue
                if (index == cursorColumn) { cursor = text.length; cursorWidth = cell.width }
                val start = text.length; text.append(cell.text)
                if (cell.width != 1 || cell.text.length != 1) glyphs.add(TerminalGlyphRun(start, text.length, cell.width, cell.style))
                if (cell.style != TerminalCellStyle()) {
                    val previous = runs.lastOrNull()
                    if (previous?.end == start && previous.style == cell.style) runs[runs.lastIndex] = previous.copy(end = text.length)
                    else runs.add(TerminalStyleRun(start, text.length, cell.style))
                }
            }
        }
        var offset = (text.length - 80_000).coerceAtLeast(0)
        if (offset < text.length && offset > 0 && text[offset].isLowSurrogate()) offset++
        glyphs.firstOrNull { it.start < offset && it.end > offset }?.let { offset = it.end }
        frame = TerminalRenderFrame(text.substring(offset), runs.filter { it.end > offset }.map { it.copy(start = (it.start - offset).coerceAtLeast(0), end = it.end - offset) },
            cursor?.takeIf { it >= offset }?.minus(offset), cursorWidth, applicationCursor, bracketedPaste,
            glyphs.filter { it.start >= offset }.map { it.copy(start = it.start - offset, end = it.end - offset) })
        return plain
    }
    private enum class State { Text, Escape, Csi, Osc, ControlString, StringEscape, Charset, IgnoredCharset }
    companion object {
        private const val graphics = "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·"
        private val basicColors = intArrayOf(0x0c0c0c, 0xc50f1f, 0x13a10e, 0xc19c00, 0x0037da, 0x881798, 0x3a96dd, 0xcccccc,
            0x767676, 0xe74856, 0x16c60c, 0xf9f1a5, 0x3b78ff, 0xb4009e, 0x61d6d6, 0xf2f2f2)
        internal fun ansiColor(index: Int): Int = when {
            index < 16 -> basicColors[index]
            index < 232 -> { val cube = index - 16; fun component(value: Int) = if (value == 0) 0 else 55 + value * 40
                (component(cube / 36) shl 16) or (component(cube / 6 % 6) shl 8) or component(cube % 6) }
            else -> { val value = 8 + (index - 232) * 10; (value shl 16) or (value shl 8) or value }
        }
    }
}

/** Unicode wide/fullwidth ranges; combining marks and emoji selectors do not consume cells. */
internal fun terminalCellWidth(code: Int): Int {
    if (Character.getType(code) in listOf(Character.NON_SPACING_MARK.toInt(), Character.ENCLOSING_MARK.toInt(), Character.COMBINING_SPACING_MARK.toInt()) ||
        code == 0x200d || code in 0xfe00..0xfe0f || code in 0xe0100..0xe01ef || code in 0x1f3fb..0x1f3ff) return 0
    return if (code in 0x1100..0x115f || code in 0x2329..0x232a || code in 0x2e80..0xa4cf && code != 0x303f ||
        code in 0xac00..0xd7a3 || code in 0xf900..0xfaff || code in 0xfe10..0xfe19 || code in 0xfe30..0xfe6f ||
        code in 0xff00..0xff60 || code in 0xffe0..0xffe6 || code in 0x1f000..0x1faff || code in 0x20000..0x3fffd) 2 else 1
}
