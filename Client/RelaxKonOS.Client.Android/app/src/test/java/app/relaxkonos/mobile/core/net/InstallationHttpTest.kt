package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class InstallationHttpTest {
    private val id = "00112233-4455-6677-8899-aabbccddeeff"
    private val operation get() = """{"operationId":"$id","service":"nginx","kind":"install","state":"running","stage":"preparing","progress":null,"problemCode":null,"createdAt":"2026-09-30T00:00:00Z","startedAt":null,"completedAt":null,"cancellable":false}"""
    private data class Request(val method: String, val route: String, val token: String?, val key: String?, val body: String)
    private suspend fun serve(status: Int, body: String?, block: suspend (String, MutableList<Request>) -> Unit) {
        val requests = mutableListOf<Request>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += Request(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestHeaders.getFirst("Authorization"),
                exchange.requestHeaders.getFirst("Idempotency-Key"), exchange.requestBody.readBytes().decodeToString())
            val bytes = body?.toByteArray()
            exchange.sendResponseHeaders(status, bytes?.size?.toLong() ?: -1)
            if (bytes != null) exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try { block("http://127.0.0.1:${server.address.port}", requests) } finally { server.stop(0) }
    }

    @Test fun `start and cancel preserve authorization original key and operation truth`() = runTest {
        serve(202, operation) { url, requests ->
            val api = RelaxKonApi("test", "test")
            assertTrue(api.startInstallation(url, "token", InstallationKind.Install, NginxInstallationRequest(true), "original") is ApiResult.Success)
            val result = api.cancelInstallation(url, "token", id, "cancel-original") as ApiResult.Success
            assertEquals(InstallationState.Running, result.value.state)
            assertEquals(listOf("/api/v1.0/installations/Nginx/Install", "/api/v1.0/installations/$id/cancel"), requests.map { it.route })
            assertEquals(listOf("original", "cancel-original"), requests.map { it.key })
            assertTrue(requests.all { it.method == "POST" && it.token == "Bearer token" })
        }
    }

    @Test fun `empty active task differs from missing operation and coded refusal`() = runTest {
        serve(404, null) { url, _ ->
            val api = RelaxKonApi("test", "test")
            assertEquals(ApiResult.Success<InstallationOperation?>(null), api.activeInstallation(url, "token", InstallationService.Nginx))
            assertTrue(api.installation(url, "token", id) is ApiResult.Problem)
        }
        serve(404, """{"problemCode":"host.feature_unavailable"}""") { url, _ ->
            assertTrue(RelaxKonApi("test", "test").activeInstallation(url, "token", InstallationService.Nginx) is ApiResult.Problem)
        }
    }

    @Test fun `package upload uses dedicated package MIME field and returns only a reference`() = runTest {
        serve(200, """{"id":"ref","fileName":"runtime.gz","length":7,"expiresAt":"2026-09-30T00:20:00Z"}""") { url, requests ->
            val result = RelaxKonApi("test", "test").uploadInstallationPackage(url, "token", InstallationService.Mihomo,
                "runtime.gz", 7, { "package".byteInputStream() })
            assertEquals("ref", (result as ApiResult.Success).value.id)
            assertEquals("/api/v1.0/installations/Mihomo/package", requests.single().route)
            assertTrue(requests.single().body.contains("name=\"package\"; filename=\"runtime.gz\""))
            assertEquals("Bearer token", requests.single().token)
        }
    }

    @Test fun `oversized package refuses without opening a source or network request`() = runTest {
        serve(200, "{}") { url, requests ->
            val result = RelaxKonApi("test", "test").uploadInstallationPackage(url, "token", InstallationService.Nginx,
                "huge.zip", 128L * 1024 * 1024 + 1, { error("must not read") })
            assertEquals(InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE, (result as ApiResult.Problem).code)
            assertTrue(requests.isEmpty())
        }
    }
}
