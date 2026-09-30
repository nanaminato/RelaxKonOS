package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class SmbHttpTest {
    @Test fun `all synchronous changes use current routes without invented idempotency headers`() = runTest {
        val requests = mutableListOf<List<String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestBody.readBytes().decodeToString(),
                exchange.requestHeaders.getFirst("Authorization"), exchange.requestHeaders.getFirst("Idempotency-Key").orEmpty())
            val bytes = """{"operationId":"11111111-1111-1111-1111-111111111111","succeeded":true,"problemCode":null}""".toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            val share = SmbShareRequest("共有", "/srv/share", null, true, false, false, listOf(SmbPermission("alice", SmbAccess.Read)))
            val changes = listOf(SmbChange(SmbChangeKind.Start), SmbChange(SmbChangeKind.Stop), SmbChange(SmbChangeKind.Restart),
                SmbChange(SmbChangeKind.CreateShare, share = share), SmbChange(SmbChangeKind.UpdateShare, "a b", share),
                SmbChange(SmbChangeKind.DeleteShare, "a b"), SmbChange(SmbChangeKind.EnableUser, "alice"),
                SmbChange(SmbChangeKind.DisableUser, "alice"), SmbChange(SmbChangeKind.Password, "alice"))
            changes.forEach { assertTrue(api.smbChange(url, "token", it, "private-secret".toCharArray()) is ApiResult.Success) }
            assertEquals(listOf("POST", "POST", "POST", "POST", "PUT", "DELETE", "POST", "POST", "PUT"), requests.map { it[0] })
            assertEquals(listOf("start", "stop", "restart", "shares", "shares/a%20b", "shares/a%20b", "users/alice/enable", "users/alice/disable", "users/alice/password"), requests.map { it[1].removePrefix(SmbRoutes.ROOT + "/") })
            assertTrue(requests.all { it[3] == "Bearer token" && it[4].isEmpty() })
            assertEquals("private-secret", JSONObject(requests.last()[2]).getString("password"))
            assertTrue(requests.take(8).none { it[2].contains("private-secret") })
            assertTrue(requests[5][2].isEmpty()); assertFalse(JSONObject(requests[4][2]).getBoolean("enabled"))
        } finally { server.stop(0) }
    }
}
