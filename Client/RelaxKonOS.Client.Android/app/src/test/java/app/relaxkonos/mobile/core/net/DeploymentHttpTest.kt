package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class DeploymentHttpTest {
    private suspend fun <T> serve(status: Int, body: String?, block: suspend (String, MutableList<String>) -> T): T {
        val requests = mutableListOf<String>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += "${exchange.requestMethod} ${exchange.requestURI} ${exchange.requestHeaders.getFirst("Authorization")}"
            val bytes = body?.toByteArray()
            exchange.sendResponseHeaders(status, bytes?.size?.toLong() ?: -1)
            if (bytes != null) exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        return try { block("http://127.0.0.1:${server.address.port}", requests) } finally { server.stop(0) }
    }

    @Test fun `application list uses authenticated GET on current route`() = runTest {
        serve(200, "[]") { url, requests ->
            assertEquals(ApiResult.Success(emptyList<DeploymentApplication>()), RelaxKonApi("test", "test").deploymentApplications(url, "token"))
            assertEquals(listOf("GET /api/v1.0/application-deployments/applications Bearer token"), requests)
        }
    }

    @Test fun `bodyless authorization responses preserve HTTP verdicts`() = runTest {
        for (status in listOf(401, 403, 404)) serve(status, null) { url, _ ->
            val result = RelaxKonApi("test", "test").deploymentApplications(url, "token")
            assertEquals(status, (result as ApiResult.Problem).status)
        }
    }

    @Test fun `unstructured server failure is not a permission verdict`() = runTest {
        serve(503, "unavailable") { url, _ ->
            assertTrue(RelaxKonApi("test", "test").deploymentApplications(url, "token") is ApiResult.Transport)
        }
    }

    @Test fun `malformed successful response cannot become an empty application list`() = runTest {
        serve(200, "{}") { url, _ ->
            assertTrue(RelaxKonApi("test", "test").deploymentApplications(url, "token") is ApiResult.Transport)
        }
    }

    @Test fun `domain problem remains readable even with service unavailable status`() = runTest {
        serve(503, """{"problemCode":"application-deployment.store_unavailable","traceId":"trace-1"}""") { url, _ ->
            assertEquals(ApiResult.Problem(503, "application-deployment.store_unavailable", "trace-1"),
                RelaxKonApi("test", "test").deploymentApplications(url, "token"))
        }
    }
}
