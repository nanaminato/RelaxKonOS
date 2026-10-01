package app.relaxkonos.mobile.core.net

import app.relaxkonos.mobile.deploymentFixture
import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class DeploymentRevisionHttpTest {
    private val baseline = deploymentFixture()
    private val accepted = """{"operationId":"d3708cc7-3e7e-42ad-b498-11466a48af24","applicationId":"${baseline.id}","kind":"deploy","state":"queued","stage":"preflight","progress":null,"problemCode":null,"recoveryProblemCode":null,"createdAt":"2026-10-01T00:00:00Z","cancellable":true}"""
    @Test fun `revision HTTP sends exact version and source without definition or secret fields`() = runTest {
        var payload = ""; var key: String? = null; var auth: String? = null; var route = ""
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            route = exchange.requestMethod + " " + exchange.requestURI; auth = exchange.requestHeaders.getFirst("Authorization"); key = exchange.requestHeaders.getFirst("Idempotency-Key")
            payload = exchange.requestBody.bufferedReader().use { it.readText() }
            val bytes = accepted.toByteArray(); exchange.sendResponseHeaders(202, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").deployRevision("http://127.0.0.1:${server.address.port}", "token", baseline.id,
                DeploymentRevisionSource(archiveReferenceId = "opaque", programEntry = "app.dll", arguments = listOf("", " spaced "), selfContained = true), baseline.updatedAt, "revision-key")
            assertTrue(result is ApiResult.Success); assertEquals("POST ${ApplicationDeploymentRoutes.deploy(baseline.id)}", route)
            assertEquals("Bearer token", auth); assertEquals("revision-key", key)
            val body = JSONObject(payload); assertEquals(baseline.updatedAt, body.getString("expectedUpdatedAt")); assertTrue(body.getBoolean("confirmed"))
            assertEquals(3, body.length()); val source = body.getJSONObject("source")
            assertEquals("opaque", source.getString("archiveReferenceId")); assertTrue(source.getBoolean("selfContained"))
            assertEquals("", source.getJSONArray("arguments").getString(0)); assertEquals(" spaced ", source.getJSONArray("arguments").getString(1))
            assertFalse(source.has("configuration")); assertFalse(source.has("siteId")); assertFalse(source.has("name"))
        } finally { server.stop(0) }
    }
    @Test fun `catalog HTTP submits only reviewed exact version baseline and confirmation`() = runTest {
        val target = CatalogTemplate("1", "personal-site", "2.0.0", "RelaxKonOS", "built-in", true, "website", "test", listOf("linux/amd64"), emptyList(), CatalogResources(null, null, null), emptyList(), emptyList(), 80, "/", "retain data", false)
        val preview = CatalogApplicationUpdatePreview(baseline.id, baseline.updatedAt, null, "1.0.0", target, "nginx:1.26", "nginx:1.27", "notes", emptyList())
        var payload = ""; var route = ""
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            route = exchange.requestMethod + " " + exchange.requestURI; payload = exchange.requestBody.bufferedReader().use { it.readText() }
            val bytes = accepted.toByteArray(); exchange.sendResponseHeaders(202, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try {
            assertTrue(RelaxKonApi("test", "test").updateCatalogApplication("http://127.0.0.1:${server.address.port}", "token", preview, "catalog-key") is ApiResult.Success)
            assertEquals("POST ${ApplicationDeploymentRoutes.application(baseline.id)}/catalog-update", route)
            val body = JSONObject(payload); assertEquals("personal-site", body.getString("templateId")); assertEquals("2.0.0", body.getString("templateVersion"))
            assertEquals(baseline.updatedAt, body.getString("expectedUpdatedAt")); assertEquals("1.0.0", body.getString("currentTemplateVersion"))
            assertTrue(body.has("expectedRevisionId") && body.isNull("expectedRevisionId")); assertTrue(body.getBoolean("confirmed")); assertEquals(6, body.length())
            assertFalse(body.has("imageReference")); assertFalse(body.has("fields")); assertFalse(body.has("configuration"))
        } finally { server.stop(0) }
    }
    @Test fun `catalog installation sends explicit host port for HTTP readiness`() = runTest {
        val target = CatalogTemplate("1", "personal-site", "1.0.0", "RelaxKonOS", "built-in", true, "website", "test", listOf("linux/amd64"), emptyList(), CatalogResources(null, null, null), emptyList(), emptyList(), 80, "/", "retain data", false)
        var payload = ""
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            payload = exchange.requestBody.bufferedReader().use { it.readText() }
            val bytes = "{\"operation\":$accepted}".toByteArray(); exchange.sendResponseHeaders(202, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try {
            assertTrue(RelaxKonApi("test", "test").installCatalogApplication("http://127.0.0.1:${server.address.port}", "token", target, "new-site", 8088, emptyList(), "install-key") is ApiResult.Success)
            assertEquals(8088, JSONObject(payload).getInt("hostPort"))
        } finally { server.stop(0) }
    }

}
