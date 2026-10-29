package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class TailscaleWireTest {
    private val id = "11111111-1111-1111-1111-111111111111"
    private val revision = "A".repeat(64)
    private val config = """{"acceptDns":true,"acceptRoutes":false}"""
    private val control = """{"revision":"$revision","connected":true,"authenticated":true,"configuration":$config,"canControl":false}"""
    private val operation = """{"operationId":"$id","action":"Connect","state":"Unknown","expectedRevision":"$revision","expiresAt":"2026-10-29T12:00:00Z","updatedAt":"2026-10-29T11:59:00Z"}"""
    @Test fun `current control and configuration decode without native arguments`() {
        val value = TailscaleWire.control(control)
        assertTrue(value.connected); assertFalse(value.canControl); assertEquals(TailscaleConfiguration(true,false),value.configuration)
    }
    @Test fun `configuration request carries only structured values`() {
        val body = JSONObject(String(TailscalePlanRequest(id,revision,TailscaleAction.Configure,TailscaleConfiguration(false,true)).body().toByteArray()))
        assertEquals(setOf("operationId","expectedRevision","action","configuration"),body.keys().asSequence().toSet())
        assertFalse(body.getJSONObject("configuration").getBoolean("acceptDns"))
        assertEquals("Configure",body.getString("action"))
    }
    @Test fun `configuration presence must match action`() {
        assertTrue(runCatching { TailscalePlanRequest(id,revision,TailscaleAction.Connect,TailscaleConfiguration(true,false)).body() }.isFailure)
        assertTrue(runCatching { TailscalePlanRequest(id,revision,TailscaleAction.Configure).body() }.isFailure)
    }
    @Test fun `paths reject non canonical IDs and injection`() {
        listOf("../other","$id/execute","00000000-0000-0000-0000-000000000000","1-1-1-1-1").forEach {
            assertTrue(runCatching { TailscaleWire.operationRoute(it) }.isFailure)
        }
        assertEquals("/api/v1.0/tailscale/operations/$id",TailscaleWire.operationRoute(id))
    }
    @Test fun `unknown operation states and string booleans fail closed`() {
        assertTrue(runCatching { TailscaleWire.operation(operation.replace("Unknown","Future")) }.isFailure)
        assertTrue(runCatching { TailscaleWire.control(control.replace("\"connected\":true","\"connected\":\"true\"")) }.isFailure)
    }
    @Test fun `closure remains separate from unknown operation result`() {
        val closure = JSONObject().put("operationId",id).put("observation",JSONObject(control)).put("closedAt","2026-10-29T12:00:00Z").put("actor",id)
        val raw = JSONObject(operation).put("closure",closure)
        val value = TailscaleWire.operation(raw.toString())
        assertEquals(TailscaleOperationState.Unknown,value.state); assertNotNull(value.closure)
        raw.put("state","Applied")
        assertTrue(runCatching { TailscaleWire.operation(raw.toString()) }.isFailure)
    }
    @Test fun `recovery requires observation and deadline before offering closure`() {
        val raw = JSONObject().put("operationId",id).put("canClose",true)
        assertTrue(runCatching { TailscaleWire.recovery(raw.toString()) }.isFailure)
        raw.put("observation",JSONObject(control)).put("expiresAt","2026-10-29T12:00:00Z")
        assertTrue(TailscaleWire.recovery(raw.toString()).canClose)
    }
}
