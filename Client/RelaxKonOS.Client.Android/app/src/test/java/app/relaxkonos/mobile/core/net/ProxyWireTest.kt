package app.relaxkonos.mobile.core.net

import org.junit.Assert.*
import org.junit.Test

internal const val PROXY_ID = "11111111-1111-1111-1111-111111111111"
internal const val PROXY_PROFILE = """{"id":"11111111-1111-1111-1111-111111111111","name":"Work","engineId":"mihomo","isActive":true,"revision":2}"""
internal const val PROXY_OVERVIEW = """{"runtime":{"mode":"managed","state":"running","version":"1.19.0","previousVersion":null,"integrityVerified":true,"problemCode":""},"activeProfile":null,"health":{"tunState":"disabled","controllerReachable":true,"managementRouteSafe":true,"state":"healthy","problemCode":""},"engineCapabilities":{"supportsGroups":true,"supportsConfigurationValidation":true,"supportsConnections":true,"supportsBoundedLogs":true,"supportsDnsStatus":true},"platformCapabilities":{"supportsTun":false,"supportsAutoRoute":false,"supportsDnsHijack":false},"operatingSystem":"linux","recovery":{"recoveryRequired":false}}"""
class ProxyWireTest {
    @Test fun `current overview and profile enums are strict`() {
        assertEquals(ProxyRuntimeState.Running, ProxyWire.overview(PROXY_OVERVIEW).runtime.state)
        assertFalse(ProxyWire.overview(PROXY_OVERVIEW).supportsTun)
        assertTrue(runCatching { ProxyWire.overview(PROXY_OVERVIEW.replace("\"running\"", "\"Running\"")) }.isFailure)
        assertEquals(2L, ProxyWire.profile(PROXY_PROFILE).revision)
        assertTrue(runCatching { ProxyWire.profile(PROXY_PROFILE.replace("\"revision\":2", "\"revision\":0")) }.isFailure)
        assertTrue(runCatching { ProxyWire.profile(PROXY_PROFILE.replace("mihomo", "legacy")) }.isFailure)
    }
    @Test fun `opaque unicode group names and IDs cannot add routes`() {
        assertEquals("/api/v1.0/proxy/groups/%E6%9D%B1%E4%BA%AC%2Fa%20b/selection", ProxyRoutes.selection("東京/a b"))
        assertTrue(ProxyRoutes.delay("group", "a?b#c").contains("a%3Fb%23c"))
        assertTrue(runCatching { ProxyRoutes.profile("../settings") }.isFailure)
        assertTrue(runCatching { ProxyRoutes.selection("bad\nname") }.isFailure)
    }
    @Test fun `selector semantics and delay timeout are retained`() {
        val groups = ProxyWire.groups("""[{"name":"G","type":"Selector","selected":"A","proxies":["A","B"]},{"name":"Auto","type":"URLTest","selected":"A","proxies":["A"]}]""")
        assertTrue(groups[0].selectable); assertFalse(groups[1].selectable)
        assertNull(ProxyWire.delay("""{"proxyName":"A","delayMilliseconds":null,"timedOut":true,"problemCode":""}""").delayMilliseconds)
        assertTrue(runCatching { ProxyWire.delay("""{"proxyName":"A","delayMilliseconds":-1,"timedOut":false,"problemCode":""}""") }.isFailure)
    }
    @Test fun `subscription URL is redacted and metadata never carries source`() {
        val request = ProxyImportRequest("https://example.test/private-secret", "A", ProxyDownloadRoute.SystemProxy)
        assertFalse(request.toString().contains("private-secret"))
        val body = request.body().toString()
        assertFalse(body.contains("private-secret")) // JsonBody intentionally does not expose payload through toString.
        val subscription = ProxyWire.subscription("""{"id":"$PROXY_ID","name":"A","profileId":"$PROXY_ID","isActive":true,"lastUpdatedAt":null}""")
        assertNull(subscription.lastUpdatedAtMillis)
    }
}
