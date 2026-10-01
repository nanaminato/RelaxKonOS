package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Test

class ProcessHttpTest {
    @Test fun `process query sends selected ordering and filter to the server`() = runTest {
        val requests = mutableListOf<String>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += "${exchange.requestMethod} ${exchange.requestURI}"
            val body = """{"items":[{"id":42,"name":"worker","cpuPercent":12.5,"memoryBytes":1024,"userName":"app","threadCount":3,"startTime":"2026-10-01T00:00:00.1234567Z"}],"totalCount":1,"sampledAt":"2026-10-01T00:00:10Z"}"""
            val bytes = body.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").queryProcesses(
                "http://127.0.0.1:${server.address.port}", "token", 1, 50, null, ProcessSort.Cpu, true,
            ) as ApiResult.Success<ProcessPage>

            assertEquals(12.5, result.value.items.single().cpuPercent, 0.0)
            RelaxKonApi("test", "test").queryProcesses("http://127.0.0.1:${server.address.port}", "token", 2, 50, "app user", ProcessSort.Name, false)
            assertEquals(
                listOf("GET /api/v1.0/system/processes/query?page=1&pageSize=50&sort=cpu&direction=desc",
                    "GET /api/v1.0/system/processes/query?page=2&pageSize=50&sort=name&direction=asc&filter=app+user"),
                requests,
            )
        } finally {
            server.stop(0)
        }
    }
}
