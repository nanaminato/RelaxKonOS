package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.time.Instant

class TailscaleWireTest {
    private val id = "11111111-1111-1111-1111-111111111111"
    private val revision = "A".repeat(64)
    private val config = """{"acceptDns":true,"acceptRoutes":false}"""
    private val preferences = """{"acceptDns":true,"acceptRoutes":false,"shieldsUp":true,"exitNode":"100.64.0.9","exitNodeAllowLanAccess":true,"unattended":false,"autoUpdate":true,"advertiseExitNode":true}"""
    private val control = """{"revision":"$revision","connected":true,"authenticated":true,"configuration":$config,"canControl":false}"""
    private val controlWithPreferences = """{"revision":"$revision","connected":true,"authenticated":true,"configuration":$config,"canControl":true,"preferences":$preferences,"platform":"linux"}"""
    private val operation = """{"operationId":"$id","action":"Connect","state":"Unknown","expectedRevision":"$revision","expiresAt":"2026-10-29T12:00:00Z","updatedAt":"2026-10-29T11:59:00Z"}"""
    @Test fun `current control and configuration decode without native arguments`() {
        val value = TailscaleWire.control(control)
        assertTrue(value.connected); assertFalse(value.canControl); assertEquals(TailscaleConfiguration(true,false),value.configuration)
    }
    @Test fun `closed preferences decode with platform and exit node`() {
        val value = TailscaleWire.control(controlWithPreferences)
        val expected = TailscalePreferences(true,false,true,"100.64.0.9",true,false,true,true)
        assertEquals(expected,value.preferences); assertEquals("linux",value.platform)
        assertEquals(expected,value.preferences)
    }
    @Test fun `configuration request carries only structured values`() {
        val body = JSONObject(String(TailscalePlanRequest(id,revision,TailscaleAction.Configure,TailscaleConfiguration(false,true)).body().toByteArray()))
        assertEquals(setOf("operationId","expectedRevision","action","configuration"),body.keys().asSequence().toSet())
        assertFalse(body.getJSONObject("configuration").getBoolean("acceptDns"))
        assertEquals("Configure",body.getString("action"))
    }
    @Test fun `preference request carries only closed preference values`() {
        val value = TailscalePreferences(true,false,true,"auto:any",false,false,true,false)
        val body = JSONObject(String(TailscalePlanRequest(id,revision,TailscaleAction.ConfigurePreferences,preferences = value).body().toByteArray()))
        assertEquals(setOf("operationId","expectedRevision","action","preferences"),body.keys().asSequence().toSet())
        assertEquals("auto:any",body.getJSONObject("preferences").getString("exitNode"))
        assertEquals("ConfigurePreferences",body.getString("action"))
        val logout = JSONObject(String(TailscalePlanRequest(id,revision,TailscaleAction.Logout).body().toByteArray()))
        assertEquals(setOf("operationId","expectedRevision","action"),logout.keys().asSequence().toSet())
    }
    @Test fun `configuration presence must match action`() {
        assertTrue(runCatching { TailscalePlanRequest(id,revision,TailscaleAction.Connect,TailscaleConfiguration(true,false)).body() }.isFailure)
        assertTrue(runCatching { TailscalePlanRequest(id,revision,TailscaleAction.Configure).body() }.isFailure)
        assertTrue(runCatching { TailscalePlanRequest(id,revision,TailscaleAction.ConfigurePreferences).body() }.isFailure)
        assertTrue(runCatching { TailscalePlanRequest(id,revision,TailscaleAction.Configure,TailscaleConfiguration(true,false),TailscalePreferences(true,false,false,null,false,false,false,false)).body() }.isFailure)
        assertTrue(runCatching { TailscalePlanRequest(id,revision,TailscaleAction.Logout,TailscaleConfiguration(true,false)).body() }.isFailure)
    }
    @Test fun `status decodes account tailnet os owner and exit node`() {
        val peer = """{"id":"node1","hostName":"host","dnsName":"host.example","addresses":["100.64.0.1"],"online":true,"exitNode":true,"os":"linux","tags":["server"],"exitNodeOption":true,"owner":"owner@example.com","lastSeen":"2026-10-29T12:00:00Z"}"""
        val raw = """{"state":"observed","version":"1.2","backendState":"Running","self":$peer,"peers":[$peer],"peersRestricted":false,"tailnetName":"example.ts.net","magicDnsSuffix":"example.ts.net","account":{"loginName":"owner@example.com","displayName":"Owner"},"exitNode":$peer}"""
        val value = TailscaleWire.status(raw)
        assertEquals("example.ts.net",value.tailnetName); assertEquals("Owner",value.account?.displayName)
        assertEquals("owner@example.com",value.peers.single().owner); assertEquals("linux",value.peers.single().os)
        assertEquals(listOf("server"),value.peers.single().tags); assertEquals("node1",value.exitNode?.id)
    }
    @Test fun `preference and logout operations decode with closed payloads`() {
        val prefs = TailscaleOperation(id,TailscaleAction.ConfigurePreferences,TailscaleOperationState.Applied,revision,
            Instant.parse("2026-10-29T12:00:00Z"),Instant.parse("2026-10-29T11:59:00Z"),null,null,null,
            TailscalePreferences(true,false,false,null,false,false,false,false))
        val raw = JSONObject().put("operationId",id).put("action","ConfigurePreferences").put("state","Applied")
            .put("expectedRevision",revision).put("expiresAt","2026-10-29T12:00:00Z").put("updatedAt","2026-10-29T11:59:00Z")
            .put("preferences",JSONObject(prefs.preferences!!.json().toString()))
        assertEquals(prefs.preferences,TailscaleWire.operation(raw.toString()).preferences)
        raw.remove("preferences"); raw.put("action","Logout")
        val logout = TailscaleWire.operation(raw.toString())
        assertEquals(TailscaleAction.Logout,logout.action); assertNull(logout.configuration); assertNull(logout.preferences)
        raw.put("action","ConfigurePreferences")
        assertTrue(runCatching { TailscaleWire.operation(raw.toString()) }.isFailure)
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
