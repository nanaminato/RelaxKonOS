package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class TailscaleRepositoryTest {
    @Test fun `unknown wire state is rejected instead of treated as missing installation`() {
        assertTrue(runCatching { TailscaleWire.status("""{"state":"future-state","peersRestricted":false}""") }.isFailure)
    }
    @Test fun `wire preserves missing installation and failure states`() {
        listOf("not-installed", "unavailable", "unsupported", "timeout", "invalid-response").forEach { state ->
            val result = TailscaleWire.status("""{"state":"$state","peersRestricted":false}""")
            assertEquals(state, result.state)
            assertNull(result.self)
            assertNull(result.version)
            assertTrue(result.peers.isEmpty())
        }
    }
    @Test fun `restricted peers are never shown even if response contains them`() {
        val result = TailscaleWire.status("""{"state":"observed","peersRestricted":true,"peers":[{"unexpected":true}]}""")
        assertTrue(result.peersRestricted)
        assertTrue(result.peers.isEmpty())
    }
    @Test fun `wire decodes host and device facts`() {
        val peer = """{"id":"node1","hostName":"host","dnsName":"host.example","addresses":["100.64.0.1","fd00::1"],"online":true,"exitNode":false}"""
        val result = TailscaleWire.status("""{"state":"observed","version":"1.2","backendState":"Running","self":$peer,"peers":[$peer],"peersRestricted":false}""")
        assertEquals("Running", result.backendState)
        assertEquals(listOf("100.64.0.1", "fd00::1"), result.self!!.addresses)
        assertEquals(result.self, result.peers.single())
    }
    @Test fun `host switch during response discards old facts`() = runTest {
        val base = FakeGateway()
        base.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.TAILSCALE)))
        }) }
        val session = AuthSession(base)
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        val owner = session.state.value as SessionState.Active
        val gateway = object : RelaxKonGateway by base {
            override suspend fun tailscaleStatus(serverUrl: String, accessToken: String): ApiResult<TailscaleStatus> {
                session.login(ServerConnectionIdentityRules.direct("https://other.test"), "alice", "pw".toCharArray()) {}
                return ApiResult.Success(TailscaleStatus("observed", null, null, null, emptyList(), false))
            }
        }
        try {
            TailscaleRepository(gateway, session).status(owner)
            fail("Old host facts were accepted")
        } catch (_: CancellationException) { }
    }
    @Test fun `capability absence does not send a status request`() = runTest {
        val gateway = FakeGateway()
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = emptySet()))
        }) }
        val session = AuthSession(gateway)
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        try {
            TailscaleRepository(gateway, session).status(session.state.value as SessionState.Active)
            fail("Missing capability was accepted")
        } catch (_: IllegalArgumentException) { }
    }
}
