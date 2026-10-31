package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ServerModulesWireTest {
    private fun row() = JSONObject("""{"id":"relaxkonos.git","serviceId":"host-one","effectiveEnabled":true,"desiredEnabled":false,
        "restartRequired":true,"desiredRevision":"revision","processGeneration":"generation","installedVersion":"1.0",
        "requires":[],"requiredBy":[],"capabilities":[],"residency":{"state":"dormant","strategy":"IdleDelayed","activeLeases":0,"blockedBy":null,"failure":null}}""")
    @Test fun `dormant and desired disabled remain distinct from effective enablement`() {
        val fact = ServerModulesWire.facts(JSONArray().put(row()).toString()).single()
        assertTrue(fact.effectiveEnabled)
        assertFalse(fact.desiredEnabled)
        assertTrue(fact.restartRequired)
        assertEquals("dormant", fact.residencyState)
        assertEquals("host-one", fact.serviceId)
    }
    @Test fun `legacy enabled and coerced booleans are rejected`() {
        val legacy = row().apply { remove("effectiveEnabled"); put("enabled", true) }
        assertTrue(runCatching { ServerModulesWire.facts(JSONArray().put(legacy).toString()) }.isFailure)
        assertTrue(runCatching { ServerModulesWire.facts(JSONArray().put(row().put("desiredEnabled", "false")).toString()) }.isFailure)
    }
    @Test fun `duplicate module identities and mixed servers are rejected`() {
        assertTrue(runCatching { ServerModulesWire.facts(JSONArray().put(row()).put(row()).toString()) }.isFailure)
        assertTrue(runCatching { ServerModulesWire.facts(JSONArray().put(row()).put(row().put("id", "other").put("serviceId", "other-host")).toString()) }.isFailure)
    }
    @Test fun `desired and effective residency policies remain independent facts`() {
        val value = row().put("desiredResidencyPolicy", JSONObject().put("strategy", "KeepLoaded").put("idleGraceSeconds", 42))
            .put("effectiveResidencyPolicy", JSONObject().put("strategy", "IdleDelayed").put("idleGraceSeconds", 300))
        val fact = ServerModulesWire.facts(JSONArray().put(value).toString()).single()
        assertEquals(ServerModuleResidencyPolicy("KeepLoaded", 42), fact.desiredPolicy)
        assertEquals(ServerModuleResidencyPolicy("IdleDelayed", 300), fact.effectivePolicy)
    }
    @Test fun `policy plan freezes strategy and integer grace and rejects malformed settings`() {
        val value = JSONObject("""{"planId":"10000000-0000-0000-0000-000000000001","serviceId":"host-one","expiresAt":"2099-01-01T00:00:00Z",
            "expectedRevision":"revision","target":{"serviceId":"host-one","action":"SetResidencyPolicy","moduleIds":["relaxkonos.git"],"enabledModuleIds":["relaxkonos.git"],"expectedRevision":"revision","idempotencyKey":"key","strategy":"KeepLoaded","idleGraceSeconds":42},
            "restartRequired":true,"added":[],"removed":[],"blockedBy":[]}""")
        assertEquals(ServerModuleResidencyPolicy("KeepLoaded", 42), ServerModulesWire.plan(value.toString()).policy)
        value.getJSONObject("target").put("idleGraceSeconds", 1.5)
        assertTrue(runCatching { ServerModulesWire.plan(value.toString()) }.isFailure)
        value.getJSONObject("target").put("idleGraceSeconds", 42).put("strategy", "Unknown")
        assertTrue(runCatching { ServerModulesWire.plan(value.toString()) }.isFailure)
    }
    @Test fun `operation requires identity timestamps and state and cannot contradict member results`() {
        val value = JSONObject("""{"operationId":"20000000-0000-0000-0000-000000000001","planId":"10000000-0000-0000-0000-000000000001",
            "serviceId":"host-one","state":"Succeeded","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:01Z",
            "desiredRevision":"revision","enabledModuleIds":[],"problemCode":null,"moduleResults":[]}""")
        assertEquals("Succeeded", ServerModulesWire.operation(value.toString()).state)
        for (field in listOf("operationId", "planId", "state", "createdAt", "updatedAt", "desiredRevision")) {
            val missing = JSONObject(value.toString()).apply { remove(field) }
            assertTrue(field, runCatching { ServerModulesWire.operation(missing.toString()) }.isFailure)
        }
        value.put("moduleResults", JSONArray().put(JSONObject().put("moduleId", "relaxkonos.git").put("succeeded", false).put("problemCode", "module-active-work")))
        assertTrue(runCatching { ServerModulesWire.operation(value.toString()) }.isFailure)
    }
}
