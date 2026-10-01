package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class SystemProcessWireTest {
    private val timestamp = "2026-10-01T00:00:00.1234567Z"
    @Test fun `process instance projection preserves exact timestamp precision and page sampling time`() {
        val page = SystemProcessWire.page("""{"items":[{"id":42,"name":"worker","cpuPercent":17.5,"memoryBytes":1042,"userName":null,"threadCount":3,"startTime":"$timestamp"}],"totalCount":1,"sampledAt":"$timestamp"}""")
        assertEquals(timestamp, page.items.single().startTime); assertEquals(timestamp, page.sampledAt)
        assertEquals(17.5, page.items.single().cpuPercent, 0.0)
    }
    @Test fun `failed successful and permission receipts remain distinct and malformed verdicts are rejected`() {
        assertEquals(ProcessKillResult(false, true, "process.permission_denied", null), SystemProcessWire.kill("""{"success":false,"requiresElevation":true,"problemCode":"process.permission_denied","error":null}"""))
        assertTrue(SystemProcessWire.kill("""{"success":true,"requiresElevation":false,"problemCode":"","error":null}""").success)
        assertTrue(runCatching { SystemProcessWire.kill("""{"success":true,"requiresElevation":true,"problemCode":"","error":null}""") }.isFailure)
        assertTrue(runCatching { SystemProcessWire.kill("""{"success":"false","requiresElevation":false,"problemCode":"process.not_found","error":null}""") }.isFailure)
    }
    @Test fun `termination sends only the current instance body and preserves HTTP 200 failure verdict`() = runTest {
        val requests = mutableListOf<List<String>>(); val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestBody.readBytes().decodeToString(), exchange.requestHeaders.getFirst("Authorization"))
            val bytes = """{"success":false,"requiresElevation":false,"problemCode":"process.instance_changed","error":null}""".toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").killProcess("http://127.0.0.1:${server.address.port}", "token", 42, timestamp) as ApiResult.Success
            assertFalse(result.value.success); assertEquals("process.instance_changed", result.value.problemCode)
            val request = requests.single(); assertEquals("DELETE", request[0]); assertEquals("/api/v1.0/system/processes/42", request[1])
            assertEquals(timestamp, JSONObject(request[2]).getString("expectedStartTime")); assertEquals(1, JSONObject(request[2]).length())
            assertEquals("Bearer token", request[3])
        } finally { server.stop(0) }
    }
}
