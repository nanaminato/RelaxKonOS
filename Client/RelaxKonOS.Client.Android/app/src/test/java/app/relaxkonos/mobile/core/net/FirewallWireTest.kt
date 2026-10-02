package app.relaxkonos.mobile.core.net

import org.junit.Assert.*
import org.junit.Test

class FirewallWireTest {
    @Test fun `current status keeps unavailable distinct from disabled and requires complete fields`() {
        val status = FirewallWire.status("""{"isAvailable":false,"isEnabled":false,"backend":"ufw","version":null,"defaultIncomingPolicy":null,"defaultOutgoingPolicy":null,"problemCode":"firewall.not_supported"}""")
        assertFalse(status.isAvailable); assertNull(status.defaultIncomingPolicy)
        assertTrue(runCatching { FirewallWire.status("""{"isAvailable":true,"isEnabled":false}""") }.isFailure)
    }
    @Test fun `logical address family is retained and duplicate or invalid numbered rules are refused`() {
        val value = """{"number":1,"action":"allow","direction":"in","protocol":"tcp","source":"any","destination":"any","port":"443","addressFamily":"IPv4 + IPv6"}"""
        assertEquals("IPv4 + IPv6", FirewallWire.rules("[$value]").single().addressFamily)
        assertTrue(runCatching { FirewallWire.rules("[$value,$value]") }.isFailure)
        assertTrue(runCatching { FirewallRoutes.rule(0) }.isFailure)
    }
    @Test fun `structured drafts reject hostnames shell syntax invalid CIDR and reversed ranges`() {
        listOf("192.168.1.4/24", "::1", "2001:db8::/32", "any").forEach { assertTrue(FirewallValues.endpoint(it)) }
        listOf("example.test", "1.2.3.4;reboot", "300.1.1.1", "::1/129", "1.2.3.4/33", "1.2.3.4/", "any/0").forEach { assertFalse(it, FirewallValues.endpoint(it)) }
        listOf("1", "65535", "80:443", "any", "").forEach { assertTrue(FirewallValues.port(it)) }
        listOf("0", "65536", "443:80", "80;shutdown", "80:81:82").forEach { assertFalse(FirewallValues.port(it)) }
        assertFalse(FirewallChange(FirewallChangeKind.Delete, 1).body().toString().contains("credentialConfirmation"))
    }
}
