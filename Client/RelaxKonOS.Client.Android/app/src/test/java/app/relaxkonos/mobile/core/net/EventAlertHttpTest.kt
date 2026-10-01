package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class EventAlertHttpTest {
    private val response = """{"alertId":"a","type":"deployment.operation_failed","severity":"error","status":"suppressed","firstOccurredAt":"2026-10-01T00:00:00Z","lastOccurredAt":"2026-10-01T00:00:00Z","occurrenceCount":1,"lastEventId":"e","problemCode":"deployment.failed","acknowledgedAt":null,"acknowledgedByReference":null,"resolutionReason":"maintenance","remediationTarget":{"kind":"applicationDeployment","resourceId":"r","operationId":null}}"""
    @Test fun `actions use exact closed routes and no invented replay key`() = runTest {
        val seen = mutableListOf<Pair<String, String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            assertEquals("Bearer token", exchange.requestHeaders.getFirst("Authorization"))
            assertNull(exchange.requestHeaders.getFirst("Idempotency-Key"))
            seen += "${exchange.requestMethod} ${exchange.requestURI}" to exchange.requestBody.bufferedReader().use { it.readText() }
            val bytes = response.toByteArray(); exchange.sendResponseHeaders(200, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            AlertMutation.entries.forEach { action -> assertTrue(api.mutateAlert(url, "token", "a", action, "maintenance", "2026-10-01T00:10:00Z") is ApiResult.Success) }
            assertEquals(listOf("POST /api/v1.0/event-alerts/alerts/a/acknowledgement", "POST /api/v1.0/event-alerts/alerts/a/resolve", "POST /api/v1.0/event-alerts/alerts/a/suppression", "DELETE /api/v1.0/event-alerts/alerts/a/suppression"), seen.map { it.first })
            assertEquals(setOf("note"), JSONObject(seen[0].second).keys().asSequence().toSet())
            assertEquals(setOf("reason"), JSONObject(seen[1].second).keys().asSequence().toSet())
            val suppression = JSONObject(seen[2].second); assertEquals("maintenance", suppression.getString("reason")); assertEquals("2026-10-01T00:10:00Z", suppression.getString("expiresAt")); assertEquals(2, suppression.length())
            assertEquals("", seen[3].second)
        } finally { server.stop(0) }
    }
}
