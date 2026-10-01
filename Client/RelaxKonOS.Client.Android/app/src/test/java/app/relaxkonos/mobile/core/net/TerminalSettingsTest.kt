package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class TerminalSettingsTest {
    private val payload = """{"fontFamily":"Cascadia Mono","fontSize":14,"colorScheme":"Campbell","backgroundColor":"#0C0C0C","foregroundColor":"#CCCCCC","cursorColor":"#FFFFFF"}"""
    @Test fun `current appearance parser requires six fields and validates bounds`() {
        assertEquals(TerminalSettings.Default, TerminalSettingsWire.parse(payload))
        listOf("fontFamily", "fontSize", "colorScheme", "backgroundColor", "foregroundColor", "cursorColor").forEach { field ->
            assertTrue(runCatching { TerminalSettingsWire.parse(JSONObject(payload).apply { remove(field) }.toString()) }.isFailure)
        }
        assertTrue(runCatching { TerminalSettingsWire.parse(JSONObject(payload).put("fontSize", "14").toString()) }.isFailure)
        assertTrue(runCatching { TerminalSettingsWire.validate(TerminalSettings.Default.copy(fontSize = Double.NaN)) }.isFailure)
        assertTrue(runCatching { TerminalSettingsWire.validate(TerminalSettings.Default.copy(fontSize = 41.0)) }.isFailure)
        assertTrue(runCatching { TerminalSettingsWire.validate(TerminalSettings.Default.copy(cursorColor = "red")) }.isFailure)
        assertTrue(runCatching { TerminalSettingsWire.route("1-1-1-1-1") }.isFailure)
    }
    @Test fun `workspace endpoint roundtrips appearance without any PTY command`() = runTest {
        val requests = mutableListOf<List<String>>(); val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { e -> requests += listOf(e.requestMethod, e.requestURI.toString(), e.requestBody.readBytes().decodeToString(), e.requestHeaders.getFirst("Authorization"))
            val bytes = payload.toByteArray(); e.sendResponseHeaders(200, bytes.size.toLong()); e.responseBody.use { it.write(bytes) }; e.close() }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"; val id = "11111111-1111-1111-1111-111111111111"
            assertTrue(api.terminalSettings(url, "token", id) is ApiResult.Success)
            assertTrue(api.saveTerminalSettings(url, "token", id, TerminalSettings.Default) is ApiResult.Success)
            assertEquals(listOf("GET", "PUT"), requests.map { it[0] }); assertTrue(requests.all { it[1] == "/api/v1.0/workspaces/$id/terminal-settings" && it[3] == "Bearer token" })
            assertEquals(6, JSONObject(requests[1][2]).length()); assertEquals(14.0, JSONObject(requests[1][2]).getDouble("fontSize"), 0.0)
        } finally { server.stop(0) }
    }
}
