package app.relaxkonos.mobile.ui.editor

import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.input.TextFieldValue
import org.junit.Assert.*
import org.junit.Test

class TextEditingTest {
    @Test fun `typing a newline in a CRLF file retains existing endings and caret`() {
        val before = TextFieldValue("a\r\nb", TextRange(4))
        val after = TextFieldValue("a\r\nb\n", TextRange(5))
        val result = preserveInsertedNewlines(before, after, "crlf")
        assertEquals("a\r\nb\r\n", result.text); assertEquals(TextRange(6), result.selection)
    }
    @Test fun `pasted replacement normalizes inserted lines without rewriting existing text`() {
        val result = preserveInsertedNewlines(TextFieldValue("a\r\nb\r\nc"), TextFieldValue("a\r\nx\ny\r\nc", TextRange(6)), "crlf")
        assertEquals("a\r\nx\r\ny\r\nc", result.text); assertEquals(TextRange(7), result.selection)
    }
    @Test fun `mixed file and selection changes preserve content exactly`() {
        val after = TextFieldValue("a\r\nb\nc", TextRange(1, 4))
        assertEquals(after, preserveInsertedNewlines(TextFieldValue(after.text), after, "mixed"))
    }
    @Test fun `deletion and CR files retain valid cursor positions`() {
        val result = preserveInsertedNewlines(TextFieldValue("ab"), TextFieldValue("a\nb", TextRange(2)), "cr")
        assertEquals("a\rb", result.text); assertEquals(TextRange(2), result.selection)
        assertEquals("a", preserveInsertedNewlines(TextFieldValue("a\r\nb"), TextFieldValue("a", TextRange(1)), "crlf").text)
    }
}
