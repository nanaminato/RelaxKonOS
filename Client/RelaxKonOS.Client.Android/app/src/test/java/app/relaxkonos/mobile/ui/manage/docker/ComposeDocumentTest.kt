package app.relaxkonos.mobile.ui.manage.docker

import java.io.ByteArrayInputStream
import java.nio.charset.CharacterCodingException
import org.junit.Assert.assertEquals
import org.junit.Test

class ComposeDocumentTest {
    @Test fun preservesUnicodeSource() {
        val source = "services:\n  app:\n    environment:\n      LABEL: 日本語中文\n"
        assertEquals(source, readComposeDocument(ByteArrayInputStream(source.toByteArray(Charsets.UTF_8))))
    }
    @Test fun acceptsExactByteLimit() {
        assertEquals(1024 * 1024, readComposeDocument(ByteArrayInputStream(ByteArray(1024 * 1024) { 32 })).length)
    }
    @Test(expected = ComposeDocumentTooLargeException::class) fun rejectsOversizeInsteadOfTruncating() {
        readComposeDocument(ByteArrayInputStream(ByteArray(1024 * 1024 + 1) { 32 }))
    }
    @Test(expected = CharacterCodingException::class) fun rejectsMalformedUtf8InsteadOfChangingSource() {
        readComposeDocument(ByteArrayInputStream(byteArrayOf(0xC3.toByte(), 0x28)))
    }
}
