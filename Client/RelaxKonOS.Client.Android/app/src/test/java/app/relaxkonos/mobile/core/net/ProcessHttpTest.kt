package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Test

class ProcessHttpTest {
    @Test fun `process query uses CPU sort contract and reads CPU usage`() = runTest {
        val requests = mutableListOf<String>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += "${exchange.requestMethod} ${exchange.requestURI}"
            val body = """{"items":[{"id":42,"name":"worker","cpuPercent":12.5,"memoryBytes":1024,"userName":"app","threadCount":3}],"totalCount":1}"""
            val bytes = body.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").queryProcesses(
                "http://127.0.0.1:${server.address.port}", "token", 1, 50, null,
            ) as ApiResult.Success<ProcessPage>

            assertEquals(12.5, result.value.items.single().cpuPercent, 0.0)
            assertEquals(
                listOf("GET /api/v1.0/system/processes/query?page=1&pageSize=50&sort=cpu&direction=desc"),
                requests,
            )
        } finally {
            server.stop(0)
        }
    }
}
