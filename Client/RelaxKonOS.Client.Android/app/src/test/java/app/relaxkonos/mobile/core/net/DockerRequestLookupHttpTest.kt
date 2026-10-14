package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class DockerRequestLookupHttpTest {
    @Test fun `request lookup is an authenticated read with the exact original header`() = runBlocking {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        val observed = mutableListOf<List<String>>()
        server.createContext("/") { exchange ->
            observed += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestHeaders.getFirst("Authorization"),
                exchange.requestHeaders.getFirst("Idempotency-Key"), exchange.requestBody.readBytes().decodeToString())
            exchange.sendResponseHeaders(404, -1); exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").dockerStackOperationByRequest("http://127.0.0.1:${server.address.port}", "access", "original-key")
            assertTrue(result is ApiResult.Problem); assertEquals(404, (result as ApiResult.Problem).status)
            assertEquals(listOf("GET", "/api/v1.0/docker/stack-operations/by-request", "Bearer access", "original-key", ""), observed.single())
        } finally { server.stop(0) }
    }
}
