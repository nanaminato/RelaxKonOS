package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class FirewallHttpTest {
    @Test fun `all mutation routes use current structured bodies without passwords`() = runTest {
        val requests = mutableListOf<List<String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestBody.readBytes().decodeToString(),
                exchange.requestHeaders.getFirst("Authorization"), exchange.requestHeaders.getFirst("Idempotency-Key").orEmpty())
            val bytes = """{"success":true,"problemCode":""}""".toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            val rule = FirewallRule(7, "allow", "in", "tcp", "192.0.2.0/24", "any", "443", "IPv4")
            val changes = listOf(FirewallChange(FirewallChangeKind.Enabled, enabled = true), FirewallChange(FirewallChangeKind.Defaults, incoming = "deny", outgoing = "allow"),
                FirewallChange(FirewallChangeKind.Create, rule = rule), FirewallChange(FirewallChangeKind.Replace, 7, rule = rule), FirewallChange(FirewallChangeKind.Delete, 7))
            changes.forEach { assertTrue(api.changeFirewall(url, "token", it) is ApiResult.Success) }
            assertEquals(listOf("PUT", "PUT", "POST", "PUT", "DELETE"), requests.map { it[0] })
            assertEquals(listOf("enabled", "defaults", "rules", "rules/7", "rules/7"), requests.map { it[1].removePrefix("/api/v1.0/firewall/") })
            assertTrue(requests.all { it[3] == "Bearer token" && it[4].isEmpty() })
            assertTrue(requests.all { !JSONObject(it[2]).has("credentialConfirmation") })
            assertEquals("192.0.2.0/24", JSONObject(requests[3][2]).getString("source"))
        } finally { server.stop(0) }
    }
}
