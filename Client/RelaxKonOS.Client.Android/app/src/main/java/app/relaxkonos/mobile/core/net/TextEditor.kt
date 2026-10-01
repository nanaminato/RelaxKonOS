package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import java.nio.charset.CodingErrorAction
import java.nio.CharBuffer
import org.json.JSONObject

data class RemoteTextFile(val path: String, val content: String, val version: String,
    val encoding: String, val bom: Boolean, val newline: String)

object TextEditorPolicy {
    const val MAXIMUM_BYTES = 256 * 1024
    val encodings = listOf("utf-8", "utf-16le", "utf-16be", "utf-32le", "utf-32be")
    fun valid(content: String, encoding: String, bom: Boolean): Boolean = runCatching {
        require(encoding in encodings)
        require(encoding == "utf-8" || bom)
        require(content.none { it < ' ' && it != '\t' && it != '\r' && it != '\n' })
        val encoder = java.nio.charset.Charset.forName(encoding).newEncoder()
            .onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
        val bytes = encoder.encode(CharBuffer.wrap(content)).remaining()
        bytes + (if (!bom) 0 else when (encoding) { "utf-8" -> 3; "utf-16le", "utf-16be" -> 2; else -> 4 }) <= MAXIMUM_BYTES
    }.getOrDefault(false)
}

object TextEditorRoutes {
    fun file(path: String) = "/api/v1.0/files/text?path=${URLEncoder.encode(path, "UTF-8")}"
}

internal object TextEditorWire {
    fun file(payload: String): RemoteTextFile = JSONObject(payload).let {
        val file = RemoteTextFile(it.getString("path"), it.getString("content"), it.getString("version"),
            it.getString("encoding"), it.getBoolean("bom"), it.getString("newline"))
        require(file.path.isNotBlank() && file.version.matches(Regex("[0-9a-f]{64}")))
        require(file.newline in listOf("lf", "crlf", "cr", "mixed", "none"))
        require(TextEditorPolicy.valid(file.content, file.encoding, file.bom))
        file
    }
}
