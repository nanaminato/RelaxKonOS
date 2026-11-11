package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class MobileRecoveryHttpTest {
    private val operationId = "13393223-1aae-4b27-8ad4-8ebba32a1d77"
    private val targetId = "ab403d7d-5933-491e-8938-cf939b329931"
    private val api = RelaxKonApi("test", "test")

    private data class Request(val method: String, val route: String, val key: String?, val body: String)

    private suspend fun serve(response: String, call: suspend (String, List<Request>) -> Unit) {
        val requests = mutableListOf<Request>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += Request(exchange.requestMethod, exchange.requestURI.toString(),
                exchange.requestHeaders.getFirst("Idempotency-Key"), exchange.requestBody.readBytes().decodeToString())
            val bytes = response.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.start()
        try { call("http://127.0.0.1:${server.address.port}", requests) } finally { server.stop(0) }
    }

    @Test fun `topology is read for an exact template version and port`() = runTest {
        val template = CatalogTemplate("2", "web-db", "1.0", "Official", "built-in", true, "Web", "",
            listOf("linux/amd64"), listOf("server.application-deployments.compose"), CatalogResources(null, null, null),
            emptyList(), emptyList(), 8080, "/", "", false)
        serve("""{"templateId":"web-db","templateVersion":"1.0","bindAddress":"127.0.0.1","hostPort":8081,
            "webService":"web","healthPath":"/health","services":[{"id":"web","imageReference":"web:1","containerPort":8080,
            "dependsOn":["db"],"volumes":[]},{"id":"db","imageReference":"db:1","containerPort":5432,"dependsOn":[],
            "volumes":[{"name":"database","containerPath":"/data","readOnly":false}]}]}""") { url, requests ->
            val topology = (api.previewCatalogTopology(url, "token", template, 8081) as ApiResult.Success).value
            assertTrue(template.requiresTopology)
            assertTrue(topology.matches(template, 8081))
            assertFalse(topology.matches(template, 8082))
            assertFalse(topology.matches(template.copy(version = "2.0"), 8081))
            assertEquals("db", topology.services.first().dependsOn.single())
            assertEquals("/data", topology.services.last().volumes.single().containerPath)
            assertEquals(Request("GET", "/api/v1.0/application-deployments/catalog/web-db/1.0/topology?hostPort=8081", null, ""), requests.single())
        }
    }

    @Test fun `retained data reinstall submits only revision and confirmation with original key`() = runTest {
        serve("""{"operationId":"$operationId","applicationId":"$targetId","kind":"reinstall","state":"queued","stage":"queued",
            "progress":null,"problemCode":null,"recoveryProblemCode":null,"createdAt":"2026-11-11T00:00:00Z","cancellable":true}""") { url, requests ->
            val result = api.reinstallCatalogApplication(url, "token", targetId, operationId, "original-key")
            assertEquals("reinstall", (result as ApiResult.Success).value.kind)
            val request = requests.single()
            assertEquals("POST", request.method)
            assertEquals("/api/v1.0/application-deployments/applications/$targetId/catalog-reinstall", request.route)
            assertEquals("original-key", request.key)
            val body = JSONObject(request.body)
            assertEquals(setOf("revisionId", "confirmed"), body.keys().asSequence().toSet())
            assertEquals(operationId, body.getString("revisionId"))
            assertTrue(body.getBoolean("confirmed"))
        }
    }

    @Test fun `website recovery uses original operation routes and receipt`() = runTest {
        serve("""{"operationId":"$operationId","applicationId":"$targetId","webServerId":"nginx",
            "domain":"example.test","siteId":"site","certificateId":"$targetId","certificateOperationId":"$operationId",
            "state":"interrupted","stage":"completed","problemCode":"website.interrupted",
            "recoveryProblemCode":"website.site_application_unverified","interruptedFromStage":"certificate","checks":[]}""") { url, requests ->
            for (action in BootstrapRecovery.entries) {
                val result = api.recoverWebsiteBootstrap(url, "token", operationId, action) as ApiResult.Success
                assertEquals(operationId, result.value.operationId)
                assertTrue(result.value.canResumeBootstrap)
                assertTrue(result.value.canCleanupBootstrap)
            }
            assertEquals(BootstrapRecovery.entries.map { "/api/v1.0/website-publications/operations/$operationId/${it.route}" },
                requests.map { it.route })
            assertTrue(requests.all { it.method == "POST" })
        }
    }

    @Test fun `Nginx compensation selects only the known original operation kinds`() = runTest {
        serve("""{"operationId":"$operationId","instanceId":"nginx","kind":"integrate","state":"running",
            "stage":"integration-compensation-dispatch","problemCode":"webserver.native_recovery_required","compensationOperationId":"$targetId"}""") { url, requests ->
            val original = WebServerOperation(operationId, "nginx", "integrate", WebServerOperationState.Running,
                "reload", "webserver.native_recovery_required", null, null, null)
            val result = api.compensateWebServer(url, "token", original) as ApiResult.Success
            assertEquals(targetId, result.value.compensationOperationId)
            api.compensateWebServer(url, "token", original.copy(kind = "enable-acme-http01"))
            assertNull(original.copy(kind = "reload").compensationRoute())
            assertEquals(listOf("compensate-integration", "compensate-webroot").map { "/api/v1.0/webservers/operations/$operationId/$it" },
                requests.map { it.route })
            assertTrue(requests.all { it.method == "POST" && JSONObject(it.body).getBoolean("confirmed") })
        }
    }

    @Test fun `ACME recovery reads evidence and sends only the confirmed plan token`() = runTest {
        val receipt = """{"operationId":"$operationId","certificateId":"$targetId","kind":"renew","state":"failed",
            "stage":"completed","problemCode":"certificate.operation_interrupted"}"""
        val orderRoute = "/api/v1.0/certificates/operations/$operationId/acme-order"
        serve("""{"operation":$receipt,"remoteStatus":"valid"}""") { url, requests ->
            val result = api.certificateRecovery(url, "token", operationId, CertificateRecoveryRead.Observe) as ApiResult.Success
            assertEquals("valid", result.value.status)
            assertEquals(orderRoute, requests.single().route)
        }
        serve("""{"operation":$receipt,"status":"ready","target":"kestrel","issuedFingerprintSha256":"issued","planToken":"opaque"}""") { url, requests ->
            val result = api.certificateRecovery(url, "token", operationId, CertificateRecoveryRead.Plan) as ApiResult.Success
            assertEquals("opaque", result.value.planToken)
            assertEquals("issued", result.value.fingerprint)
            assertEquals("$orderRoute/certificate/kestrel-plan", requests.single().route)
        }
        serve("""{"operation":$receipt,"status":"applied","fingerprintSha256":"issued"}""") { url, requests ->
            val result = api.applyCertificateRecovery(url, "token", operationId, "opaque") as ApiResult.Success
            assertEquals("applied", result.value.status)
            val request = requests.single()
            assertEquals("$orderRoute/certificate/kestrel-recovery", request.route)
            val body = JSONObject(request.body)
            assertEquals(setOf("confirmed", "planToken"), body.keys().asSequence().toSet())
            assertTrue(body.getBoolean("confirmed"))
            assertEquals("opaque", body.getString("planToken"))
        }
    }
}
