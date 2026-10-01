package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class TextEditorTest {
    @Test fun `current response requires explicit encoding BOM newline and byte version`() {
        val file = TextEditorWire.file(payload())
        assertEquals("utf-8", file.encoding); assertFalse(file.bom); assertEquals("lf", file.newline)
        listOf("encoding", "bom", "newline", "version").forEach { field ->
            try { TextEditorWire.file(JSONObject(payload()).apply { remove(field) }.toString()); fail(field) } catch (_: Exception) { }
        }
        try { TextEditorWire.file(JSONObject(payload()).put("version", "old").toString()); fail() } catch (_: Exception) { }
    }
    @Test fun `binary malformed text and encoded byte limits are enforced`() {
        assertTrue(TextEditorPolicy.valid("hello 世界 🐱\r\n", "utf-16be", true))
        assertFalse(TextEditorPolicy.valid("hello", "utf-16le", false))
        assertFalse(TextEditorPolicy.valid("hello\u0000", "utf-8", false))
        assertFalse(TextEditorPolicy.valid("hello\ud800", "utf-8", false))
        assertFalse(TextEditorPolicy.valid("界".repeat(TextEditorPolicy.MAXIMUM_BYTES / 3 + 1), "utf-8", false))
        assertTrue(TextEditorPolicy.valid("a".repeat(TextEditorPolicy.MAXIMUM_BYTES), "utf-8", false))
        assertFalse(TextEditorPolicy.valid("a".repeat(TextEditorPolicy.MAXIMUM_BYTES), "utf-8", true))
    }
    @Test fun `file and Git editor send current conditional format and encoded path without task keys`() = runTest {
        val requests = mutableListOf<List<String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { e ->
            requests += listOf(e.requestMethod, e.requestURI.toString(), e.requestBody.readBytes().decodeToString(),
                e.requestHeaders.getFirst("Authorization"), e.requestHeaders.getFirst("Idempotency-Key").orEmpty())
            val bytes = payload().toByteArray()
            e.sendResponseHeaders(200, bytes.size.toLong()); e.responseBody.use { it.write(bytes) }; e.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            val file = TextEditorWire.file(payload())
            assertTrue(api.textFile(url, "token", "/配置 a.json") is ApiResult.Success)
            assertTrue(api.saveTextFile(url, "token", file, "new\r\n") is ApiResult.Success)
            assertTrue(api.createTextFile(url, "token", "/new.txt", "new", "utf-16be", true) is ApiResult.Success)
            assertTrue(api.gitSaveTextFile(url, "token", "repo", file, "git") is ApiResult.Success)
            assertEquals(listOf("GET", "PUT", "POST", "PUT"), requests.map { it[0] })
            assertTrue(requests[0][1].contains("%E9%85%8D%E7%BD%AE+a.json"))
            listOf(requests[1], requests[3]).forEach {
                val body = JSONObject(it[2]); assertEquals(file.version, body.getString("expectedVersion"))
                assertEquals(file.encoding, body.getString("encoding")); assertFalse(body.getBoolean("bom"))
            }
            assertFalse(JSONObject(requests[2][2]).has("expectedVersion"))
            assertTrue(requests.all { it[3] == "Bearer token" && it[4].isEmpty() })
        } finally { server.stop(0) }
    }
    private fun payload() = """{"path":"/a.txt","content":"hello\n","version":"${"a".repeat(64)}","encoding":"utf-8","bom":false,"newline":"lf"}"""
}
