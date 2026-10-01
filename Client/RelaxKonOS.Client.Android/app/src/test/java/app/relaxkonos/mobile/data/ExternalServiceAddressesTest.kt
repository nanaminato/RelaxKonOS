package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.deploymentFixture
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.*
import org.junit.Assert.*
import org.junit.Test

class ExternalServiceAddressesTest {
    private fun owner(serviceId: String = "https://api.example.test/api-root") = SessionState.Active(serviceId, "http://127.0.0.1:40000/api-root", "alice", "workspace",
        emptySet(), "linux", ExecutionEligibility(true, null, true), workspaceId = "workspace")
    private fun site(tls: Boolean = false, redirect: Boolean = false, domain: String = "site.example.test") = WebServerSite("site", "server", "name",
        listOf(WebServerBinding(domain, 8088)), emptyList(), null, tls, 0, "2026-10-01T00:00:00Z", null, false, redirect, false, null, null)
    @Test fun `dangerous schemes userinfo malformed ports and wildcard hosts are rejected`() {
        listOf("javascript:alert(1)", "file:///tmp/file", "intent://example.test/", "http://user:password@example.test/", "http://example.test:0/",
            "http://example.test:70000/", "http://0.0.0.0/", "http://[::]/", "https://example.test/#fragment", "http://example.test/\n").forEach { assertFalse(it, ExternalServiceAddresses.valid(it)) }
        assertTrue(ExternalServiceAddresses.valid("https://example.test/path?a=b"))
    }
    @Test fun `deployment uses published workload port and stable origin host without API path`() {
        val app = deploymentFixture().copy(workloadKind = "web", actualState = "running", bindAddress = "0.0.0.0", hostPort = 8088)
        assertEquals("http://api.example.test:8088/", ExternalServiceAddresses.deployment(owner(), app)?.url)
        assertNull(ExternalServiceAddresses.deployment(owner(), app.copy(bindAddress = "127.0.0.1")))
        assertNull(ExternalServiceAddresses.deployment(owner(), app.copy(workloadKind = "worker")))
        assertNull(ExternalServiceAddresses.deployment(owner(), app.copy(actualState = "stopped")))
    }
    @Test fun `managed login address is never reused as a remote workload address`() {
        val app = deploymentFixture().copy(workloadKind = "web", actualState = "running", bindAddress = "0.0.0.0", hostPort = 8088)
        assertNull(ExternalServiceAddresses.deployment(owner("rki-" + "a".repeat(32)), app))
        assertNull(ExternalServiceAddresses.deployment(owner("http://localhost:8080"), app))
    }
    @Test fun `site TLS uses actual server listener contract and redirect suppresses plain HTTP`() {
        assertEquals(listOf("http://site.example.test:8088/"), ExternalServiceAddresses.site(site()).map { it.url })
        assertEquals(listOf("https://site.example.test/", "http://site.example.test:8088/"), ExternalServiceAddresses.site(site(true)).map { it.url })
        assertEquals(listOf("https://site.example.test/"), ExternalServiceAddresses.site(site(true, true)).map { it.url })
        listOf("*.example.test", "_", "localhost", "127.0.0.1", "user@evil.test").forEach { assertTrue(ExternalServiceAddresses.site(site(domain = it)).isEmpty()) }
    }
    @Test fun `IPv6 and international domains create single safe authorities`() {
        assertEquals("http://[2001:db8::1]:8088/", ExternalServiceAddresses.site(site(domain = "2001:db8::1")).single().url)
        assertTrue(ExternalServiceAddresses.site(site(domain = "例え.test")).single().url.startsWith("http://xn--"))
    }
    @Test fun `only a running user forward exposes its actual assigned port and reviewed path`() {
        val row = SshLocalForward("id", "host", SshLocalForwardRequest(8080, null, "https", "/ui?a=b"), 12000, SshForwardStatus.Running, 0)
        assertEquals(ExternalServiceAddress("https://127.0.0.1:12000/ui?a=b", true), ExternalServiceAddresses.forward(row))
        assertNull(ExternalServiceAddresses.forward(row.copy(status = SshForwardStatus.Stopped)))
        assertNull(ExternalServiceAddresses.forward(row.copy(request = row.request.copy(pathAndQuery = "//evil.test/"))))
    }
}
