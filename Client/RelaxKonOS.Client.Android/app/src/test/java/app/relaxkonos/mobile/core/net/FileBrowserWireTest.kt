package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class FileBrowserWireTest {
    private val properties = """{"path":"/srv/ 文件 * ","name":" 文件 * ","type":"file","size":4,"created":null,"modified":null,"accessed":"2026-10-01T00:00:00Z","permissions":"-rwsr-xr-x","attributes":"Normal","unixMode":2541}"""
    @Test fun `properties and permissions use current routes and preserve whitespace paths and special bits`() = runTest {
        val requests = mutableListOf<List<String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { e ->
            requests += listOf(e.requestMethod, e.requestURI.toString(), e.requestBody.readBytes().decodeToString(), e.requestHeaders.getFirst("Authorization"))
            val bytes = properties.toByteArray(); e.sendResponseHeaders(200, bytes.size.toLong()); e.responseBody.use { it.write(bytes) }; e.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            val read = api.fileProperties(url, "token", "/srv/ 文件 * ") as ApiResult.Success
            assertEquals(2541, read.value.unixMode); assertEquals("Normal", read.value.attributes); assertNotNull(read.value.accessedMillis)
            assertTrue(api.setFilePermissions(url, "token", read.value.path, 2541) is ApiResult.Success)
            assertEquals("GET", requests[0][0]); assertTrue(requests[0][1].startsWith("/api/v1.0/files/properties?path="))
            assertTrue(requests[0][1].contains("%E6%96%87%E4%BB%B6"))
            assertEquals("PUT", requests[1][0]); assertEquals("/api/v1.0/files/permissions", requests[1][1])
            assertEquals("/srv/ 文件 * ", JSONObject(requests[1][2]).getString("path")); assertEquals(2541, JSONObject(requests[1][2]).getInt("unixMode"))
            assertTrue(requests.all { it[3] == "Bearer token" })
        } finally { server.stop(0) }
    }
    @Test fun `listing consumes host flags and drive kinds and malformed properties do not become usable`() = runTest {
        var response = """{"path":"","name":"Computer","directories":[{"path":"C:\\","name":"C:","type":"drive","size":100,"modified":null,"isHidden":false,"isSystem":true}],"files":[{"path":"C:\\hidden","name":"hidden","size":4,"modified":null,"mimeType":null,"isHidden":true,"isSystem":false}]}"""
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { e -> val bytes = response.toByteArray(); e.sendResponseHeaders(200, bytes.size.toLong()); e.responseBody.use { it.write(bytes) }; e.close() }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            val read = api.listDirectory(url, "t", "") as ApiResult.Success
            assertTrue(read.value.entries.first().isDrive); assertTrue(read.value.entries.first().isSystem)
            assertTrue(read.value.entries.last().isHidden)
            response = JSONObject(properties).put("unixMode", 4096).toString()
            assertTrue(api.fileProperties(url, "t", "/srv/ 文件 * ") is ApiResult.Transport)
            response = JSONObject(properties).apply { remove("attributes") }.toString()
            assertTrue(api.fileProperties(url, "t", "/srv/ 文件 * ") is ApiResult.Transport)
        } finally { server.stop(0) }
    }
}
