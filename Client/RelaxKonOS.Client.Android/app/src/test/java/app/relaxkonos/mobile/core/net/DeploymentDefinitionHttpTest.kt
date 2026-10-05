package app.relaxkonos.mobile.core.net

import app.relaxkonos.mobile.deploymentFixture
import app.relaxkonos.mobile.ui.manage.deployments.DeploymentDefinitionDraft
import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class DeploymentDefinitionHttpTest {
    @Test fun `PUT sends exact optimistic version full definition explicit nulls and secret references on current route`() = runTest {
        val baseline = deploymentFixture()
        val request = DeploymentDefinitionDraft(baseline).also { it.name = "renamed"; it.readiness = "process"; it.siteId = "" }.requestOrNull()!!
        val response = """{"id":"${baseline.id}","name":"renamed","sourceKind":"image","workloadKind":"web","desiredState":"running","actualState":"running",
            "readinessLevel":"process","containerPort":8080,"hostPort":9080,"bindAddress":"127.0.0.1","currentRevisionNumber":1,
            "containerName":"container","siteId":null,"domain":"website.test","driftProblemCode":null,"catalogTemplateId":"personal-site","catalogTemplateVersion":"1.0.0",
            "healthCheckPath":null,"limits":{"cpuCores":1.5,"memoryBytes":16777217,"pidsLimit":512},"volumes":[{"name":"data","containerPath":"/app/data:live","readOnly":true}],
            "configuration":[{"name":"TEXT","value":" value=kept ","isSecret":false,"secretVersion":null},{"name":"TOKEN","value":"saved-secret","isSecret":true,"secretVersion":7}],
            "updatedAt":"2026-10-01T00:00:01.1234567+00:00"}"""
        var method = ""; var path = ""; var key: String? = null; var bearer: String? = null; var payload = ""
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            method = exchange.requestMethod; path = exchange.requestURI.toString(); key = exchange.requestHeaders.getFirst("Idempotency-Key")
            bearer = exchange.requestHeaders.getFirst("Authorization"); payload = exchange.requestBody.bufferedReader().use { it.readText() }
            val bytes = response.toByteArray(); exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").updateDeploymentDefinition("http://127.0.0.1:${server.address.port}", "token", baseline.id, request, "save-1")
            val app = (result as ApiResult.Success).value
            assertEquals("PUT", method); assertEquals(ApplicationDeploymentRoutes.application(baseline.id), path)
            assertEquals("save-1", key); assertEquals("Bearer token", bearer)
            val body = JSONObject(payload)
            assertEquals(baseline.updatedAt, body.getString("expectedUpdatedAt"))
            assertEquals(16777217L, body.getJSONObject("limits").getLong("memoryBytes"))
            assertTrue(body.has("siteId") && body.isNull("siteId")); assertTrue(body.has("healthCheckPath") && body.isNull("healthCheckPath"))
            assertTrue(body.getJSONArray("volumes").getJSONObject(0).getBoolean("readOnly"))
            assertEquals("/app/data:live", body.getJSONArray("volumes").getJSONObject(0).getString("containerPath"))
            val secret = body.getJSONArray("configuration").getJSONObject(1)
            assertEquals("saved-secret", secret.getString("value")); assertEquals(7, secret.getInt("secretVersion"))
            assertEquals(" value=kept ", body.getJSONArray("configuration").getJSONObject(0).getString("value"))
            assertFalse(body.has("sourceKind")); assertFalse(body.has("catalogTemplateVersion")); assertFalse(body.has("confirmed"))
            assertEquals("1.0.0", app.catalogTemplateVersion); assertEquals("saved-secret", app.configuration.last().value)
            assertEquals("2026-10-01T00:00:01.1234567+00:00", app.updatedAt)
        } finally { server.stop(0) }
    }
}
