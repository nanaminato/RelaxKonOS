package app.relaxkonos.mobile.ui.manage.tunnels

import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class TunnelDraftTest {
    @Test fun `managed profile normalizes IDN and IPv6 without exposing Token`() {
        val request = TunnelProfileDraft(name = "edge", host = "bücher.example.").request()!!
        assertEquals("xn--bcher-kva.example", request.host); assertNull(request.externalPath)
        assertNotNull(TunnelProfileDraft(name = "edge", host = "2001:db8::1").request())
        assertNull(TunnelProfileDraft(name = "bad name", host = "host.test").request())
    }
    @Test fun `external runtime accepts host absolute paths and rejects relative paths`() {
        val draft = TunnelProfileDraft(name = "edge", host = "host.test", mode = TunnelRuntimeMode.External)
        assertNull(draft.copy(path = "frpc").request())
        assertEquals("C:\\FRP\\frpc.exe", draft.copy(path = "C:\\FRP\\frpc.exe").request()!!.externalPath)
        assertNotNull(draft.copy(path = "/opt/frp/frpc").request())
    }
    @Test fun `TCP UDP require remote port and HTTP HTTPS clear inactive remote port`() {
        val draft = TunnelDefinitionDraft(profileId = TUNNEL_PROFILE_ID, name = "web", localPort = "8080", remotePort = "6000", domain = "app.example.test")
        assertEquals(6000, draft.request()!!.remotePort); assertNull(draft.request()!!.domain)
        assertNull(draft.copy(remotePort = "65536").request())
        val http = draft.copy(protocol = TunnelProtocol.Https).request()!!
        assertNull(http.remotePort); assertEquals("app.example.test", http.domain)
        assertNull(draft.copy(protocol = TunnelProtocol.Http, domain = "*.example.test").request())
    }
    @Test fun `editing retains original revision instead of silently accepting newer facts`() {
        val record = TunnelWire.definition(TUNNEL_JSON)
        assertEquals(2L, TunnelDefinitionDraft.from(record).copy(name = "changed").request()!!.expectedRevision)
        assertEquals(3L, TunnelProfileDraft.from(TunnelWire.profile(TUNNEL_PROFILE_JSON)).request()!!.expectedRevision)
    }
}
