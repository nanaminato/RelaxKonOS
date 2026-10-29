package app.relaxkonos.mobile.core.net

import org.junit.Assert.*
import org.junit.Test

class TailscaleLoginWireTest {
    private val id = "11111111-1111-1111-1111-111111111111"
    @Test fun `official origin accepts only exact ASCII tokens`() {
        assertTrue(TailscaleLoginWire.officialUrl("https://login.tailscale.com/a/0123456789_ABC-"))
        listOf("http://login.tailscale.com/a/0123456789","https://login.tailscale.com:443/a/0123456789",
            "https://login.tailscale.com.evil/a/0123456789","https://login.tailscale.com/a/0123456789?secret=x",
            "https://login.tailscale.com/a/0123456789#x","https://login.tailscale.com/a/%300123456789",
            "https://login.tailscale.com/a/0123456789/extra","https://login.tailscale.com/a/短字符串不是ASCII", "https://login.tailscale.com/a/short")
            .forEach { assertFalse(it,TailscaleLoginWire.officialUrl(it)) }
    }
    @Test fun `challenge diagnostics hide the URL`() {
        val result = TailscaleLoginWire.challenge("""{"operationId":"$id","authenticationUrl":"https://login.tailscale.com/a/0123456789abcdef","expiresAt":"2026-10-29T12:00:00Z"}""")
        assertFalse(result.toString().contains("0123456789abcdef"))
    }
    @Test fun `challenge with foreign origin fails parsing`() {
        assertTrue(runCatching { TailscaleLoginWire.challenge("""{"operationId":"$id","authenticationUrl":"https://example.test/login","expiresAt":"2026-10-29T12:00:00Z"}""") }.isFailure)
    }
    @Test fun `operation parsing rejects unknown state and bad revision`() {
        val raw = """{"operationId":"$id","state":"Issued","expectedRevision":"${"A".repeat(64)}","expiresAt":"2026-10-29T12:00:00Z","updatedAt":"2026-10-29T11:59:00Z"}"""
        assertEquals(TailscaleLoginState.Issued,TailscaleLoginWire.operation(raw).state)
        assertTrue(runCatching { TailscaleLoginWire.operation(raw.replace("Issued","Future")) }.isFailure)
        assertTrue(runCatching { TailscaleLoginWire.operation(raw.replace("A".repeat(64),"bad")) }.isFailure)
    }
    @Test fun `recovery cannot be closable without stable observation and deadline`() {
        assertTrue(runCatching { TailscaleLoginWire.recovery("""{"operationId":"$id","canClose":true}""") }.isFailure)
        assertTrue(runCatching { TailscaleLoginWire.recovery("""{"operationId":"$id","canClose":"true"}""") }.isFailure)
    }
    @Test fun `login route never accepts arbitrary paths`() {
        assertEquals("/api/v1.0/tailscale/login/operations/$id",TailscaleLoginWire.route(id))
        assertTrue(runCatching { TailscaleLoginWire.route("../challenge") }.isFailure)
    }
}
