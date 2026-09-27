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

    @Test fun `application logs are read through bounded route`() = runTest {
        serve(200, """{"lines":["ready"],"truncated":false}""") { url, requests ->
            assertEquals(ApiResult.Success(DeploymentLog(listOf("ready"), false)),
                RelaxKonApi("test", "test").deploymentLogs(url, "token", "d3708cc7-3e7e-42ad-b498-11466a48af23"))
            assertEquals(listOf("GET /api/v1.0/application-deployments/applications/d3708cc7-3e7e-42ad-b498-11466a48af23/logs?tail=200 Bearer token"), requests)
        }
    }

    @Test fun `templates come from the server contract`() = runTest {
        serve(200, """[{"sourceKind":"javaJar","templateVersion":"1.0","displayName":"Java JAR","defaultBaseImage":"eclipse-temurin:21-jre",
            "supportedPlatforms":[],"requiresArchive":true,"requiresImageReference":false,"supportsSelfContained":false,"defaultContainerPort":8080}]""") { url, requests ->
            val templates = (RelaxKonApi("test", "test").deploymentTemplates(url, "token") as ApiResult.Success).value
            assertEquals("javaJar", templates.single().sourceKind)
            assertEquals(listOf("GET /api/v1.0/application-deployments/templates Bearer token"), requests)
        }
    }

    @Test fun `archive deployment streams staging input then submits only its opaque reference`() = runTest {
        val applicationId = "d3708cc7-3e7e-42ad-b498-11466a48af23"
        val requests = mutableListOf<Pair<String, String>>()
        val application = """{"id":"$applicationId","name":"worker","sourceKind":"pythonProject","workloadKind":"worker",
            "desiredState":"stopped","actualState":"unknown","readinessLevel":"process","containerPort":8000,
            "hostPort":null,"bindAddress":"127.0.0.1","currentRevisionNumber":null,"containerName":null,"domain":null,"driftProblemCode":null}"""
        val operation = """{"operationId":"op-archive","applicationId":"$applicationId","kind":"deploy","state":"queued",
            "stage":"queued","progress":null,"problemCode":null,"recoveryProblemCode":null,"createdAt":"2026-09-27T00:00:00Z","cancellable":true}"""
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += exchange.requestURI.path to exchange.requestBody.readBytes().decodeToString()
            val response = when {
                exchange.requestURI.path.endsWith("/uploads") -> """{"referenceId":"archive-1","fileName":"worker.zip","length":3,"expiresAt":"2026-09-27T01:00:00Z"}"""
                exchange.requestURI.path.endsWith("/deploy") -> operation
                else -> application
            }
            val bytes = response.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test")
            val definition = ArchiveDeploymentDefinition("pythonProject", "worker", 8000, "worker", "process", null,
                baseImage = "python:3.13-slim", runtimeVersion = "3.13", programEntry = "worker.main")
            val created = api.createArchiveDeployment("http://127.0.0.1:${server.address.port}", "token", definition, "create-key")
            val staged = api.uploadDeploymentArchive("http://127.0.0.1:${server.address.port}", "token", "worker.zip", 3L) {
                "zip".byteInputStream()
            }
            val deployed = api.deployArchive("http://127.0.0.1:${server.address.port}", "token", applicationId,
                (staged as ApiResult.Success).value.referenceId, definition, "deploy-key")
            assertEquals(applicationId, (created as ApiResult.Success).value.id)
            assertEquals("deploy", (deployed as ApiResult.Success).value.kind)
            assertTrue(requests[0].second.contains("\"sourceKind\":\"pythonProject\""))
            assertTrue(requests[1].second.contains("filename=\"worker.zip\""))
            assertTrue(requests[1].second.contains("zip"))
            assertTrue(requests[2].second.contains("\"archiveReferenceId\":\"archive-1\""))
            assertFalse(requests[2].second.contains("worker.zip"))
        } finally {
            server.stop(0)
        }
    }

    @Test fun `image definition and deployment use separate idempotent mutations`() = runTest {
        val requests = mutableListOf<Triple<String, String?, String>>()
        val applicationId = "d3708cc7-3e7e-42ad-b498-11466a48af23"
        val application = """{"id":"$applicationId","name":"website","sourceKind":"image","workloadKind":"web",
            "desiredState":"stopped","actualState":"unknown","readinessLevel":"http","containerPort":8080,
            "hostPort":null,"bindAddress":"127.0.0.1","currentRevisionNumber":null,"containerName":null,"domain":null,"driftProblemCode":null}"""
        val operation = """{"operationId":"op-1","applicationId":"$applicationId","kind":"deploy","state":"queued",
            "stage":"queued","progress":null,"problemCode":null,"recoveryProblemCode":null,"createdAt":"2026-09-27T00:00:00Z","cancellable":true}"""
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += Triple(exchange.requestURI.path, exchange.requestHeaders.getFirst("Idempotency-Key"), exchange.requestBody.readBytes().decodeToString())
            val response = if (exchange.requestURI.path.endsWith("/deploy")) operation else application
            val bytes = response.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test")
            val created = api.createImageDeployment("http://127.0.0.1:${server.address.port}", "token",
                ImageDeploymentDefinition("website", 8080, configuration = listOf(DeploymentConfigEntry("TOKEN", "not-in-summary", true))), "definition-key")
            assertEquals(applicationId, (created as ApiResult.Success).value.id)
            val deployed = api.deployImage("http://127.0.0.1:${server.address.port}", "token", applicationId, "nginx:1.27", "deployment-key")
            assertEquals(applicationId, (deployed as ApiResult.Success).value.applicationId)
            assertEquals(listOf("definition-key", "deployment-key"), requests.map { it.second })
            assertTrue(requests[0].third.contains("\"sourceKind\":\"image\""))
            assertTrue(requests[0].third.contains("\"name\":\"TOKEN\""))
            assertTrue(requests[0].third.contains("\"isSecret\":true"))
            assertTrue(requests[1].third.contains("\"imageReference\":\"nginx:1.27\""))
            assertTrue(requests[1].third.contains("\"confirmed\":true"))
        } finally {
            server.stop(0)
        }
    }

    @Test fun `lifecycle action uses closed route and durable idempotency key`() = runTest {
        val applicationId = "d3708cc7-3e7e-42ad-b498-11466a48af23"
        val operation = """{"operationId":"op-2","applicationId":"$applicationId","kind":"stop","state":"queued",
            "stage":"queued","progress":null,"problemCode":null,"recoveryProblemCode":null,"createdAt":"2026-09-27T00:00:00Z","cancellable":true}"""
        val requests = mutableListOf<Triple<String, String?, String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += Triple(exchange.requestURI.path, exchange.requestHeaders.getFirst("Idempotency-Key"), exchange.requestBody.readBytes().decodeToString())
            val bytes = operation.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").deploymentLifecycle("http://127.0.0.1:${server.address.port}", "token",
                applicationId, DeploymentLifecycleAction.Stop, "stop-key")
            assertEquals("stop", (result as ApiResult.Success).value.kind)
            assertEquals("/api/v1.0/application-deployments/applications/$applicationId/stop", requests.single().first)
            assertEquals("stop-key", requests.single().second)
            assertTrue(requests.single().third.contains("\"force\":false"))
            assertTrue(requests.single().third.contains("\"confirmed\":true"))
        } finally {
            server.stop(0)
        }
    }

    @Test fun `rollback targets a server known revision with confirmation and idempotency`() = runTest {
        val applicationId = "d3708cc7-3e7e-42ad-b498-11466a48af23"
        val revisionId = "2aa17c34-9f0a-45a6-a57a-c738946c312f"
        val operation = """{"operationId":"op-rollback","applicationId":"$applicationId","kind":"rollback","state":"queued",
            "stage":"queued","progress":null,"problemCode":null,"recoveryProblemCode":null,"createdAt":"2026-09-27T00:00:00Z","cancellable":true}"""
        val requests = mutableListOf<Triple<String, String?, String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += Triple(exchange.requestURI.path, exchange.requestHeaders.getFirst("Idempotency-Key"), exchange.requestBody.readBytes().decodeToString())
            val bytes = operation.toByteArray()
            exchange.sendResponseHeaders(202, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").rollbackDeployment("http://127.0.0.1:${server.address.port}", "token",
                applicationId, revisionId, "rollback-key")
            assertEquals("rollback", (result as ApiResult.Success).value.kind)
            assertEquals("/api/v1.0/application-deployments/applications/$applicationId/rollback", requests.single().first)
            assertEquals("rollback-key", requests.single().second)
            assertTrue(requests.single().third.contains("\"revisionId\":\"$revisionId\""))
            assertTrue(requests.single().third.contains("\"confirmed\":true"))
        } finally {
            server.stop(0)
        }
    }

    @Test fun `application deletion retains volumes and is idempotent`() = runTest {
        val applicationId = "d3708cc7-3e7e-42ad-b498-11466a48af23"
        val operation = """{"operationId":"op-delete","applicationId":"$applicationId","kind":"delete","state":"queued",
            "stage":"queued","progress":null,"problemCode":null,"recoveryProblemCode":null,"createdAt":"2026-09-27T00:00:00Z","cancellable":true}"""
        val requests = mutableListOf<Triple<String, String?, String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += Triple(exchange.requestMethod + " " + exchange.requestURI.path, exchange.requestHeaders.getFirst("Idempotency-Key"), exchange.requestBody.readBytes().decodeToString())
            val bytes = operation.toByteArray()
            exchange.sendResponseHeaders(202, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").deleteDeployment("http://127.0.0.1:${server.address.port}", "token", applicationId, "delete-key")
            assertEquals("delete", (result as ApiResult.Success).value.kind)
            assertEquals("DELETE /api/v1.0/application-deployments/applications/$applicationId", requests.single().first)
            assertEquals("delete-key", requests.single().second)
            assertTrue(requests.single().third.contains("\"deleteVolumes\":false"))
            assertTrue(requests.single().third.contains("\"confirmed\":false"))
        } finally {
            server.stop(0)
        }
    }

    @Test fun `cancellable operation posts only to its UUID cancel route`() = runTest {
        val operationId = "a14da5df-2a18-4f68-bfd9-e2e2934b8f36"
        val applicationId = "d3708cc7-3e7e-42ad-b498-11466a48af23"
        val operation = """{"operationId":"$operationId","applicationId":"$applicationId","kind":"deploy","state":"running",
            "stage":"pulling","progress":null,"problemCode":null,"recoveryProblemCode":null,"createdAt":"2026-09-27T00:00:00Z","cancellable":false}"""
        val requests = mutableListOf<String>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += "${exchange.requestURI.path} ${exchange.requestHeaders.getFirst("Idempotency-Key")}"
            val bytes = operation.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
            exchange.close()
        }
        server.start()
        try {
            val result = RelaxKonApi("test", "test").cancelDeploymentOperation("http://127.0.0.1:${server.address.port}", "token", operationId, "cancel-key")
            assertFalse((result as ApiResult.Success).value.cancellable)
            assertEquals(listOf("/api/v1.0/application-deployments/operations/$operationId/cancel cancel-key"), requests)
        } finally {
            server.stop(0)
        }
    }
}
