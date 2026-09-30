package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class CertificateHttpTest {
    @Test fun `live deployment GET uses the deployment path without mutation key`() = runTest {
        serve(200, KESTREL_JSON) { url, requests ->
            assertTrue(RelaxKonApi("test", "test").kestrelCertificateDeployment(url, "token", CERTIFICATE_ID) is ApiResult.Success)
            assertEquals(listOf("GET", "/api/v1.0/certificates/$CERTIFICATE_ID/deployments/kestrel", ""), requests.single().take(3))
        }
    }
    private suspend fun serve(status: Int, payload: String, call: suspend (String, List<List<String>>) -> Unit) {
        val requests = mutableListOf<List<String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            assertEquals("Bearer token", exchange.requestHeaders.getFirst("Authorization"))
            requests += listOf(exchange.requestMethod, exchange.requestURI.toString(), exchange.requestHeaders.getFirst("Idempotency-Key").orEmpty(), exchange.requestBody.readBytes().decodeToString())
            val bytes = payload.toByteArray(); exchange.sendResponseHeaders(status, bytes.size.toLong()); exchange.responseBody.use { it.write(bytes) }; exchange.close()
        }
        server.start()
        try { call("http://127.0.0.1:${server.address.port}", requests) } finally { server.stop(0) }
    }
    @Test fun `creation carries original key and current route`() = runTest {
        serve(202, CERTIFICATE_OPERATION_JSON) { url, requests ->
            assertTrue(RelaxKonApi("test", "test").certificateMutation(url, "token", CertificateAction.Issue, null, JsonBody(), "original") is ApiResult.Success)
            assertEquals(listOf("POST", "/api/v1.0/certificates", "original"), requests.single().take(3))
        }
    }
    @Test fun `delete carries confirmed JSON body and does not mistake no record for success`() = runTest {
        serve(409, """{"problemCode":"certificate.not_found"}""") { url, requests ->
            val result = RelaxKonApi("test", "test").certificateMutation(url, "token", CertificateAction.Delete, CERTIFICATE_ID, JsonBody().bool("confirmed", true), "original")
            assertEquals("certificate.not_found", (result as ApiResult.Problem).code)
            assertEquals("DELETE", requests.single()[0]); assertTrue(requests.single()[3].contains("\"confirmed\":true"))
        }
    }
    @Test fun `preflight and cancellation use their own routes without mutation keys`() = runTest {
        serve(200, CERTIFICATE_PREFLIGHT_JSON) { url, requests ->
            assertTrue(RelaxKonApi("test", "test").certificatePreflight(url, "token", listOf("a.test"), CertificateChallenge.DirectHttp01) is ApiResult.Success)
            assertEquals("/api/v1.0/certificates/preflight", requests.single()[1]); assertEquals("", requests.single()[2])
        }
        serve(200, CERTIFICATE_OPERATION_JSON) { url, requests ->
            assertTrue(RelaxKonApi("test", "test").cancelCertificateOperation(url, "token", CERTIFICATE_OPERATION_ID) is ApiResult.Success)
            assertEquals("/api/v1.0/certificates/operations/$CERTIFICATE_OPERATION_ID/cancel", requests.single()[1])
        }
    }
    @Test fun `malformed or missing operation response remains unverified`() = runTest {
        serve(202, "{}") { url, _ -> assertTrue(RelaxKonApi("test", "test").certificateMutation(url, "token", CertificateAction.SelfSigned, null, JsonBody(), "original") is ApiResult.Transport) }
        serve(404, "") { url, _ -> assertTrue(RelaxKonApi("test", "test").certificateOperation(url, "token", CERTIFICATE_OPERATION_ID) is ApiResult.Problem) }
    }
}
