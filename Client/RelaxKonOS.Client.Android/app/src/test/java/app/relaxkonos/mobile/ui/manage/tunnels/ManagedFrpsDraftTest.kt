package app.relaxkonos.mobile.ui.manage.tunnels

import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class ManagedFrpsDraftTest {
    private val draft = ManagedFrpsDraft(revision = 3, allowPorts = "6000, 6010-6020", tokenConfigured = true)
    @Test fun `bindings are literal IP addresses and ports ranges remain bounded`() {
        assertNotNull(draft.copy(bindAddress = "::1").request("".toCharArray(), "".toCharArray())); assertNull(draft.copy(bindAddress = "host.test").request("".toCharArray(), "".toCharArray()))
        assertNull(draft.copy(bindPort = "65536").request("".toCharArray(), "".toCharArray())); assertNull(draft.copy(allowPorts = "6010-6000").request("".toCharArray(), "".toCharArray()))
        assertNull(draft.copy(allowPorts = List(65) { "6000" }.joinToString(",")).request("".toCharArray(), "".toCharArray())); assertNull(draft.copy(allowPorts = "6000,").request("".toCharArray(), "".toCharArray()))
    }
    @Test fun `existing secrets survive blank replacements while first configuration requires Token`() {
        val request = draft.request("".toCharArray(), "".toCharArray())!!; assertNull(request.token); assertEquals(3L, request.expectedRevision)
        assertNull(draft.copy(tokenConfigured = false).request("".toCharArray(), "".toCharArray())); assertNotNull(draft.copy(tokenConfigured = false).request("new-token".toCharArray(), "".toCharArray()))
        assertNull(draft.request("token\nnext".toCharArray(), "".toCharArray())); assertNull(draft.request("x".repeat(4097).toCharArray(), charArrayOf()))
    }
    @Test fun `dashboard needs credentials and cannot reuse a configured listener port`() {
        val enabled = draft.copy(dashboardEnabled = true, dashboardUser = "admin")
        assertNull(enabled.request("".toCharArray(), "".toCharArray())); assertNotNull(enabled.request("".toCharArray(), "password".toCharArray()))
        assertNull(enabled.copy(dashboardPort = "7000").request("".toCharArray(), "password".toCharArray()))
        assertNull(enabled.copy(dashboardAddress = "dashboard.test").request("".toCharArray(), "password".toCharArray()))
        assertNotNull(enabled.copy(dashboardPasswordConfigured = true).request("".toCharArray(), "".toCharArray()))
    }
    @Test fun `projection keeps every safe field and original revision without copying Token`() {
        val value = ManagedFrpsWire.configuration(FRPS_JSON)
        val request = ManagedFrpsDraft.from(value).request("".toCharArray(), "".toCharArray())!!
        assertEquals(value.revision, request.expectedRevision); assertEquals(value.allowPorts, request.allowPorts)
        assertEquals(value.httpsPort, request.httpsPort); assertEquals(value.dashboardUser, request.dashboardUser); assertNull(request.token)
    }
}
