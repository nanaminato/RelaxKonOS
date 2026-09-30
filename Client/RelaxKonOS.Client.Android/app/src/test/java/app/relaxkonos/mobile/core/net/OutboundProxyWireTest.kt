package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

internal const val PROXY_STATUS = """{
  "settings":{"enabled":true,"source":"custom","httpProxy":"http://user:p%40ss@proxy:8080","httpsProxy":"","noProxy":"localhost,.example.test,10.0.0.0/8","applyToEngine":true,"applyToBuild":false,"applyToImageTags":true,"applyToRuntimeDownloads":false},
  "layers":[{"target":"engine","state":"restartRequired","problemCode":"","detail":"docker.proxy.detail.restart_pending"},{"target":"build","state":"disabled","problemCode":"","detail":""}],
  "effectiveHttpProxy":"http://http.docker.internal:3128","effectiveHttpsProxy":"","effectiveNoProxy":"localhost",
  "managedProxyEndpoint":"http://127.0.0.1:7890","managedProxyAvailable":false,"platform":"windows",
  "desktopProxy":{"mode":"manual","httpProxy":"http://upstream:8080","httpsProxy":"","noProxy":"localhost","settingsPath":"C:/settings.json","isManual":true}
}"""

class OutboundProxyWireTest {
    @Test fun `readback preserves credentials empty HTTPS independent scopes and observed upstreams`() {
        val result = OutboundProxyWire.status(PROXY_STATUS)
        assertEquals("http://user:p%40ss@proxy:8080", result.settings.httpProxy)
        assertEquals("", result.settings.httpsProxy)
        assertEquals("localhost,.example.test,10.0.0.0/8", result.settings.noProxy)
        assertFalse(result.settings.applyToBuild)
        assertTrue(result.settings.applyToImageTags)
        assertFalse(result.settings.applyToRuntimeDownloads)
        assertEquals(OutboundProxyLayerState.RestartRequired, result.layers.first().state)
        assertEquals("http://http.docker.internal:3128", result.effectiveHttpProxy)
        assertEquals("http://upstream:8080", result.desktopProxy!!.httpProxy)
        assertFalse(result.managedProxyAvailable)
    }

    @Test fun `save echoes actual secret values and sends explicit confirmation with current casing`() {
        val settings = OutboundProxyWire.status(PROXY_STATUS).settings
        val request = JSONObject(OutboundProxyWire.request(settings, true).toByteArray().decodeToString())
        assertEquals("custom", request.getString("source"))
        assertEquals(settings.httpProxy, request.getString("httpProxy"))
        assertEquals(settings.noProxy, request.getString("noProxy"))
        assertEquals("", request.getString("httpsProxy"))
        assertTrue(request.getBoolean("confirmed"))
        assertFalse(request.getBoolean("applyToRuntimeDownloads"))
        assertEquals(10, request.length())
    }

    @Test fun `managed source and non Desktop host retain failed per-layer result`() {
        val body = JSONObject(PROXY_STATUS).put("desktopProxy", JSONObject.NULL)
        body.getJSONObject("settings").put("source", "managedProxy")
        body.getJSONArray("layers").getJSONObject(0).put("state", "failed").put("problemCode", "docker.proxy.problem.managed_proxy_unavailable")
        val result = OutboundProxyWire.status(body.toString())
        assertNull(result.desktopProxy)
        assertEquals(OutboundProxySource.ManagedProxy, result.settings.source)
        assertEquals(OutboundProxyLayerState.Failed, result.layers.first().state)
        assertEquals("docker.proxy.problem.managed_proxy_unavailable", result.layers.first().problemCode)
    }

    @Test fun `invalid enums or missing scopes cannot silently become a disabled preference`() {
        val unknown = JSONObject(PROXY_STATUS)
        unknown.getJSONObject("settings").put("source", "legacy")
        assertTrue(runCatching { OutboundProxyWire.status(unknown.toString()) }.isFailure)
        val incomplete = JSONObject(PROXY_STATUS)
        incomplete.getJSONObject("settings").remove("applyToImageTags")
        assertTrue(runCatching { OutboundProxyWire.status(incomplete.toString()) }.isFailure)
    }
}
