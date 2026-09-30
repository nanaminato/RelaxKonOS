package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
class DockerResourceHttpTest {
    @Test fun `resource lifecycle uses actual methods confirmed bodies encoded identities and no task keys`() = runTest {
        val requests = mutableListOf<List<String>>(); val id = "sha256:" + "a".repeat(64)
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { e ->
            requests += listOf(e.requestMethod, e.requestURI.toString(), e.requestBody.readBytes().decodeToString(), e.requestHeaders.getFirst("Authorization"), e.requestHeaders.getFirst("Idempotency-Key").orEmpty())
            val bytes = """{"success":true,"problemCode":"","logLines":null,"logTruncated":false}""".toByteArray()
            e.sendResponseHeaders(200, bytes.size.toLong()); e.responseBody.use { it.write(bytes) }; e.close()
        }; server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            val changes = listOf(DockerResourceChange(DockerResourceAction.CreateContainer, container = DockerContainerCreate("web", "nginx", listOf("serve"))),
                DockerResourceChange(DockerResourceAction.RenameContainer, "abc123", "renamed"), DockerResourceChange(DockerResourceAction.DeleteContainer, "abc123", force = true),
                DockerResourceChange(DockerResourceAction.PullImage, value = "nginx:alpine"), DockerResourceChange(DockerResourceAction.DeleteImage, id),
                DockerResourceChange(DockerResourceAction.CreateNetwork, value = "custom", driver = "bridge"), DockerResourceChange(DockerResourceAction.DeleteNetwork, "abc123"),
                DockerResourceChange(DockerResourceAction.CreateVolume, value = "data", driver = "local", labels = listOf("team=mobile")), DockerResourceChange(DockerResourceAction.DeleteVolume, "data"))
            changes.forEach { assertTrue(api.dockerResourceChange(url, "token", it) is ApiResult.Success) }
            assertEquals(listOf("POST", "PUT", "POST", "POST", "DELETE", "POST", "DELETE", "POST", "DELETE"), requests.map { it[0] })
            assertEquals("/api/v1.0/docker/containers/abc123/delete", requests[2][1]); assertTrue(JSONObject(requests[2][2]).getBoolean("force"))
            assertEquals("renamed", JSONObject(requests[1][2]).getString("name")); assertEquals(id, JSONObject(requests[4][2]).getString("imageReference"))
            assertTrue(requests[4][1].contains("sha256%3A")); assertEquals("/api/v1.0/docker/networks/abc123?confirmed=true", requests[6][1])
            assertTrue(requests[6][2].isEmpty() && requests[8][2].isEmpty()); assertEquals("team=mobile", JSONObject(requests[7][2]).getJSONArray("labels").getString(0))
            assertTrue(requests.all { it[3] == "Bearer token" && it[4].isEmpty() })
            assertEquals(listOf("start", "stop", "restart", "pause", "unpause"), listOf(DockerResourceAction.Start, DockerResourceAction.Stop, DockerResourceAction.Restart, DockerResourceAction.Pause, DockerResourceAction.Unpause).map { DockerResourceRoutes.route(DockerResourceChange(it, "abc123")).substringAfterLast('/') })
        } finally { server.stop(0) }
    }
}
