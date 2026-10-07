package app.relaxkonos.mobile.ui.manage.docker

import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction

internal class ComposeDocumentTooLargeException : IllegalArgumentException()

/** Matches the server's UTF-8 Compose source limit; never imports a truncated document. */
internal fun readComposeDocument(input: InputStream): String {
    val limit = 1024 * 1024
    val output = ByteArrayOutputStream()
    val buffer = ByteArray(8192)
    while (true) {
        val count = input.read(buffer, 0, minOf(buffer.size, limit - output.size() + 1))
        if (count < 0) break
        if (count == 0) continue
        if (output.size() + count > limit) throw ComposeDocumentTooLargeException()
        output.write(buffer, 0, count)
    }
    return Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
        .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(output.toByteArray())).toString()
}
