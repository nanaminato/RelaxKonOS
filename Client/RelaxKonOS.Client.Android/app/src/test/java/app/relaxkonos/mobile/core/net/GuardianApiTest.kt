package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class GuardianApiTest {
    private val definition = GuardianDefinition("two words", "Job", "/bin/job", listOf("", " spaced ", "a\nb"), "/work", true,
        49, 17, GuardianHealthCheck("http", "https://host/health", 37, 11, 9), "alice", "uid:1042")
    @Test fun `current routes authorization and complete save envelope roundtrip without coercion`() = runTest {
        val requests = mutableListOf<List<String>>(); val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestBody.readBytes().decodeToString(), exchange.requestHeaders.getFirst("Authorization"))
            val payload = if (exchange.requestMethod == "GET" || exchange.requestURI.path.endsWith("/workloads")) JSONObject().put("success", true).put("problemCode", "").put("definition", JSONObject(GuardianWire.definitionJson(definition))).toString()
                else "{\"success\":true,\"problemCode\":\"\"}"
            val bytes = payload.toByteArray(); exchange.sendResponseHeaders(200, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            assertEquals(definition, (api.guardianDefinition(url, "token", definition.id) as ApiResult.Success).value.definition)
            assertTrue(api.guardianSave(url, "token", definition, GuardianApproval("root", "approval".toCharArray())) is ApiResult.Success)
            assertTrue(api.guardianAction(url, "token", definition.id, "restart") is ApiResult.Success)
            assertTrue(api.guardianDelete(url, "token", definition.id) is ApiResult.Success)
            assertEquals(listOf("GET", "POST", "POST", "DELETE"), requests.map { it[0] })
            assertEquals(GuardianRoutes.workload(definition.id), requests[0][1]); assertEquals(GuardianRoutes.WORKLOADS, requests[1][1])
            assertTrue(requests.all { it[3] == "Bearer token" })
            val body = JSONObject(requests[1][2]); assertEquals(2, body.length())
            assertEquals(11, body.getJSONObject("definition").length())
            assertEquals(definition, GuardianWire.definition(JSONObject().put("success", true).put("problemCode", "").put("definition", body.getJSONObject("definition")).toString()).definition)
            assertEquals("root", body.getJSONObject("runAsApproval").getString("username"))
            assertEquals("approval", body.getJSONObject("runAsApproval").getString("password"))
        } finally { server.stop(0) }
    }
    @Test fun `failed definition receipt and HTTP unavailable collection remain explicit failures`() = runTest {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            val collection = exchange.requestURI.path.endsWith("/workloads")
            val body = if (collection) "{\"status\":503,\"problemCode\":\"guardian.agent_timeout\"}"
                else "{\"success\":false,\"problemCode\":\"guardian.workload_not_found\",\"definition\":null}"
            exchange.responseHeaders.set("Content-Type", if (collection) "application/problem+json" else "application/json")
            val bytes = body.toByteArray(); exchange.sendResponseHeaders(if (collection) 503 else 200, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            assertFalse((api.guardianDefinition(url, "token", "missing") as ApiResult.Success).value.success)
            assertEquals("guardian.agent_timeout", (api.guardianWorkloads(url, "token") as ApiResult.Problem).code)
        } finally { server.stop(0) }
    }
}
