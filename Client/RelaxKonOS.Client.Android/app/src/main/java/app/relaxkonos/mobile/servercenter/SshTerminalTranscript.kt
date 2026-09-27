package app.relaxkonos.mobile.servercenter

/** Bounded plain-text view of PTY output. Control sequences never reach the UI or logs. */
class SshTerminalTranscript(private val maximumChars: Int = 65_536) {
    private val text = StringBuilder()
    private val sequence = StringBuilder()
    private var state = State.Text
    private var pendingCarriageReturn = false

    fun append(chunk: String): String {
        for (character in chunk) {
            if (pendingCarriageReturn) {
                if (character != '\n') clearLine()
                pendingCarriageReturn = false
            }
            when (state) {
                State.Text -> when (character) {
                    '\u001b' -> state = State.Escape
                    '\r' -> pendingCarriageReturn = true
                    '\b' -> if (text.isNotEmpty() && text.last() != '\n') text.deleteCharAt(text.lastIndex)
                    '\n', '\t' -> text.append(character)
                    else -> if (character >= ' ') text.append(character)
                }
                State.Escape -> state = when (character) {
                    '[' -> State.Csi
                    ']' -> State.Osc
                    else -> State.Text
                }
                State.Csi -> {
                    if (character in '@'..'~') {
                        if (character == 'J' && sequence.toString() == "2") text.setLength(0)
                        if (character == 'K') clearLine()
                        sequence.setLength(0)
                        state = State.Text
                    } else if (sequence.length < 32) sequence.append(character)
                }
                State.Osc -> when (character) {
                    '\u0007' -> state = State.Text
                    '\u001b' -> state = State.OscEscape
                    else -> Unit
                }
                State.OscEscape -> state = if (character == '\\') State.Text else State.Osc
            }
        }
        if (text.length > maximumChars) text.delete(0, text.length - maximumChars)
        return text.toString()
    }

    private fun clearLine() {
        text.delete(text.lastIndexOf("\n") + 1, text.length)
    }

    private enum class State { Text, Escape, Csi, Osc, OscEscape }
}
