package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

internal const val PROXY_SETTINGS = """{"systemProxyEnabled":true,"allowLan":true,"dnsEnabled":true,"ipv6Enabled":false,"unifiedDelay":true,"logLevel":"warning","mixedPort":7891,"allowInsecureSubscriptionSources":true,"systemProxyHost":"192.168.1.2","tun":{"stack":"gvisor","deviceName":"Mihomo2","autoRoute":true,"strictRoute":true,"autoDetectInterface":false,"dnsHijack":"any:53","mtu":1400},"systemProxy":{"usePac":true,"guardEnabled":true,"guardIntervalSeconds":42,"useDefaultBypass":false,"bypassList":"localhost;192.168.*"}}"""
class ProxyDiagnosticsWireTest {
    @Test fun `editing one setting retains all existing TUN and Windows proxy options`() {
        val settings = ProxyDiagnosticsWire.settings(PROXY_SETTINGS)
        val body = JSONObject(settings.copy(mixedPort = 8891, dnsEnabled = false).body().toByteArray().decodeToString())
        assertEquals(8891, body.getInt("mixedPort")); assertFalse(body.getBoolean("dnsEnabled"))
        assertEquals("gvisor", body.getJSONObject("tun").getString("stack")); assertTrue(body.getJSONObject("tun").getBoolean("strictRoute"))
        assertEquals(1400, body.getJSONObject("tun").getInt("mtu")); assertFalse(body.getJSONObject("tun").getBoolean("autoDetectInterface"))
        assertTrue(body.getJSONObject("systemProxy").getBoolean("usePac")); assertEquals(42, body.getJSONObject("systemProxy").getInt("guardIntervalSeconds"))
        assertFalse(body.getJSONObject("systemProxy").getBoolean("useDefaultBypass")); assertEquals("localhost;192.168.*", body.getJSONObject("systemProxy").getString("bypassList"))
        assertTrue(body.getBoolean("allowInsecureSubscriptionSources")); assertEquals("192.168.1.2", body.getString("systemProxyHost"))
    }
    @Test fun `interrupted marker remains recovery required independently of controller status`() {
        val recovery = ProxyDiagnosticsWire.recovery("""{"recoveryRequired":true,"hasRecoveryMarker":true,"markerCreatedAt":"2026-09-30T00:00:00Z","problemCode":"proxy.recovery_required"}""")
        assertTrue(recovery.recoveryRequired); assertTrue(recovery.hasMarker); assertNotNull(recovery.markerCreatedAtMillis)
        assertEquals("proxy.recovery_required", recovery.problemCode)
    }
    @Test fun `invalid counters and unbounded log response cannot look like a valid snapshot`() {
        val traffic = """{"uploadBytesPerSecond":1,"downloadBytesPerSecond":2,"uploadTotalBytes":3,"downloadTotalBytes":4,"memoryBytes":5,"problemCode":""}"""
        assertEquals(2L, ProxyDiagnosticsWire.traffic(traffic).downloadPerSecond)
        assertTrue(runCatching { ProxyDiagnosticsWire.traffic(traffic.replace("\"memoryBytes\":5", "\"memoryBytes\":-1")) }.isFailure)
        val entry = """{"timestamp":"2026-09-30T00:00:00Z","level":"warning","message":"redacted"}"""
        assertTrue(runCatching { ProxyDiagnosticsWire.logs(List(501) { entry }.joinToString(prefix = "[", postfix = "]")) }.isFailure)
        assertEquals("redacted", ProxyDiagnosticsWire.logs("[$entry]").single().message)
    }
    @Test fun `TUN activation carries only exact profile ID and invalid IDs are rejected`() {
        assertEquals("/api/v1.0/proxy/tun/enable", ProxyRoutes.action(ProxyAction.EnableTun, PROXY_ID))
        assertEquals("/api/v1.0/proxy/tun/emergency-disable", ProxyRoutes.action(ProxyAction.EmergencyDisableTun, null))
        assertTrue(runCatching { ProxyDiagnosticsWire.settings(PROXY_SETTINGS.replace("\"mixedPort\":7891", "\"mixedPort\":0")) }.isFailure)
    }
}
