package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import java.io.ByteArrayOutputStream
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class CloudBrowserHttpTest {
    private val first = "10000000-0000-0000-0000-000000000001"
    private val second = "10000000-0000-0000-0000-000000000002"
    @Test fun `same provider accounts use distinct list and download routes`() = runTest {
        val requests = mutableListOf<String>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += exchange.requestURI.toString()
            assertEquals("Bearer token", exchange.requestHeaders.getFirst("Authorization"))
            val body = if (exchange.requestURI.path.endsWith("entries")) """{"entries":[],"nextCursor":null}""" else "data"
            val bytes = body.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try {
            val url = "http://127.0.0.1:${server.address.port}"
            val api = RelaxKonApi("test", "test")
            assertTrue(api.cloudEntries(url, "token", first, "ref", null) is ApiResult.Success)
            assertTrue(api.cloudEntries(url, "token", second, "ref", "cursor") is ApiResult.Success)
            val out = ByteArrayOutputStream()
            assertEquals(ApiResult.Success(4L), api.cloudDownload(url, "token", first, "ref", { out }))
            assertEquals("data", out.toString("UTF-8"))
            assertEquals(CloudBrowserWire.entries(first, "ref", null), requests[0])
            assertEquals(CloudBrowserWire.entries(second, "ref", "cursor"), requests[1])
            assertEquals(CloudBrowserWire.download(first, "ref"), requests[2])
        } finally { server.stop(0) }
    }
    @Test fun `refused download never opens destination`() = runTest {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange -> exchange.sendResponseHeaders(403, -1); exchange.close() }
        server.start()
        var opened = false
        try {
            val result = RelaxKonApi("test", "test").cloudDownload("http://127.0.0.1:${server.address.port}", "token", first, "ref") {
                opened = true; ByteArrayOutputStream()
            }
            assertFalse(opened)
            assertTrue(result is ApiResult.Problem)
        } finally { server.stop(0) }
    }

    @Test fun `truncated download is not reported complete`() = runTest {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            exchange.sendResponseHeaders(200, 10)
            runCatching { exchange.responseBody.use { it.write(byteArrayOf(1, 2)) } }
            exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").cloudDownload("http://127.0.0.1:${server.address.port}", "token", first, "ref", { ByteArrayOutputStream() })
            assertTrue(result is ApiResult.Transport)
        } finally { server.stop(0) }
    }

    @Test fun `zero byte upload commits under the selected connection`() = runTest {
        uploadFixture(65536, 202) { result, requests ->
            assertTrue(result is ApiResult.Success)
            assertEquals(listOf("POST", "POST"), requests.map { it.first })
            assertTrue(requests[0].second.startsWith(CloudBrowserWire.connection(first) + "/uploads?key="))
            assertEquals(CloudBrowserWire.connection(first) + "/uploads/" + "a".repeat(32) + "/commit", requests[1].second)
        }
    }
    @Test fun `invalid chunk metadata cleans up known upload session`() = runTest {
        uploadFixture(0, 202) { result, requests ->
            assertTrue(result is ApiResult.Transport)
            assertEquals(listOf("POST", "DELETE"), requests.map { it.first })
        }
    }
    @Test fun `commit authentication failure is not replayable`() = runTest {
        uploadFixture(65536, 401) { result, requests ->
            assertTrue(result is ApiResult.Transport)
            assertEquals(listOf("POST", "POST", "DELETE"), requests.map { it.first })
        }
    }
    private suspend fun uploadFixture(chunkSize: Int, commitStatus: Int,
        check: (ApiResult<Unit>, List<Pair<String, String>>) -> Unit) {
        val requests = mutableListOf<Pair<String, String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += exchange.requestMethod to exchange.requestURI.toString()
            val request = exchange.requestBody.readBytes().decodeToString()
            if (exchange.requestURI.path.endsWith("/uploads")) {
                assertTrue(request.contains("\"targetDirectoryPath\":\"opaque-parent\""))
                assertTrue(request.contains("\"length\":0"))
                val bytes = """{"uploadId":"${"a".repeat(32)}","chunkSize":$chunkSize}""".toByteArray()
                exchange.sendResponseHeaders(200, bytes.size.toLong())
                exchange.responseBody.use { it.write(bytes) }
            } else exchange.sendResponseHeaders(if (exchange.requestMethod == "DELETE") 204 else commitStatus, -1)
            exchange.close()
        }
        server.start()
        val file = java.io.File.createTempFile("cloud-test", ".tmp")
        try {
            val result = RelaxKonApi("test", "test").cloudUpload("http://127.0.0.1:${server.address.port}", "token", first, "opaque-parent", "empty.txt", file)
            check(result, requests)
        } finally { file.delete(); server.stop(0) }
    }
}
