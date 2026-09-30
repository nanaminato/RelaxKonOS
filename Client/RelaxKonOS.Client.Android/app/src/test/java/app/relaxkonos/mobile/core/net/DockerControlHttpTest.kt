package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class DockerControlHttpTest {
    @Test fun `engine and user mirror writes use exact methods bodies without fake task keys`() = runTest {
        val requests = mutableListOf<List<String>>()
        val id = "11111111-1111-1111-1111-111111111111"
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestBody.readBytes().decodeToString(),
                exchange.requestHeaders.getFirst("Authorization"), exchange.requestHeaders.getFirst("Idempotency-Key").orEmpty())
            val payload = if (exchange.requestURI.path.contains("/engine/")) """{"success":true,"problemCode":"","status":null}"""
                else """{"id":"$id","target":"docker","name":"Mirror","endpoint":"mirror.example","isSelected":false}"""
            if (exchange.requestMethod == "DELETE" || exchange.requestURI.path.endsWith("selection")) exchange.sendResponseHeaders(204, -1)
            else { val bytes = payload.toByteArray(); exchange.sendResponseHeaders(if (exchange.requestMethod == "POST" && !exchange.requestURI.path.contains("engine")) 201 else 200, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) } }
            exchange.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            DockerEngineAction.entries.forEach { assertTrue(api.dockerEngineAction(url, "token", it, true) is ApiResult.Success) }
            assertTrue(api.dockerCreateMirror(url, "token", DockerMirrorRequest("Mirror", "https://mirror.example")) is ApiResult.Success)
            assertTrue(api.dockerUpdateMirror(url, "token", id, DockerMirrorRequest("Changed", "mirror.example:443")) is ApiResult.Success)
            assertTrue(api.dockerDeleteMirror(url, "token", id) is ApiResult.Success)
            assertTrue(api.dockerSelectMirror(url, "token", id) is ApiResult.Success)
            assertTrue(api.dockerSelectMirror(url, "token", null) is ApiResult.Success)
            assertEquals(listOf("POST", "POST", "POST", "POST", "PUT", "DELETE", "PUT", "PUT"), requests.map { it[0] })
            assertEquals(listOf("start", "stop", "restart"), requests.take(3).map { it[1].substringAfterLast('/') })
            assertTrue(requests.take(3).all { JSONObject(it[2]).getBoolean("confirmed") })
            assertEquals("Changed", JSONObject(requests[4][2]).getString("name")); assertTrue(requests[5][2].isEmpty())
            assertEquals(id, JSONObject(requests[6][2]).getString("mirrorId")); assertTrue(JSONObject(requests[7][2]).isNull("mirrorId"))
            assertTrue(requests.all { it[3] == "Bearer token" && it[4].isEmpty() })
            assertTrue(requests.drop(3).all { it[1].startsWith("/api/v1.0/image-mirrors/docker") })
        } finally { server.stop(0) }
    }
}
