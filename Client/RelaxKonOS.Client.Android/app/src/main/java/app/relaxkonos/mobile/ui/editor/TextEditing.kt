package app.relaxkonos.mobile.ui.editor

import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.input.TextFieldValue

/** Normalize only newly inserted text, leaving all existing bytes' line endings intact. */
internal fun preserveInsertedNewlines(before: TextFieldValue, after: TextFieldValue, newline: String?): TextFieldValue {
    if (before.text == after.text || newline !in listOf("crlf", "cr")) return after
    val old = before.text
    val next = after.text
    var prefix = 0
    while (prefix < minOf(old.length, next.length) && old[prefix] == next[prefix]) prefix++
    var suffix = 0
    while (suffix < minOf(old.length - prefix, next.length - prefix) && old[old.lastIndex - suffix] == next[next.lastIndex - suffix]) suffix++
    val insert = next.substring(prefix, next.length - suffix)
    val separator = if (newline == "crlf") "\r\n" else "\r"
    val normalized = insert.replace("\r\n", "\n").replace('\r', '\n').replace("\n", separator)
    if (insert == normalized) return after
    val content = next.take(prefix) + normalized + next.takeLast(suffix)
    fun offset(index: Int) = when {
        index <= prefix -> index
        index >= next.length - suffix -> index + normalized.length - insert.length
        else -> prefix + insert.take(index - prefix).replace("\r\n", "\n").replace('\r', '\n').replace("\n", separator).length
    }.coerceIn(0, content.length)
    return TextFieldValue(content, TextRange(offset(after.selection.start), offset(after.selection.end)))
}
