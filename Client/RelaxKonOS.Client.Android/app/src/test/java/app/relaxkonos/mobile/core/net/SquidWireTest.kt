package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class SquidWireTest {
    @Test fun `access modes round trip without silently downgrading authentication`() {
        SquidAccessMode.entries.forEach { mode ->
            val original = SquidConfiguration(accessMode = mode)
            assertEquals(mode, SquidWire.configuration(original.json()).accessMode)
        }
        val json = SquidConfiguration().json()
        json.remove("accessMode")
        assertTrue(runCatching { SquidWire.configuration(json) }.isFailure)
        json.put("accessMode", "unknown")
        assertTrue(runCatching { SquidWire.configuration(json) }.isFailure)
    }
    @Test fun `package capability is independent and required`() {
        val payload = JSONObject("""{"state":{"configuration":null,"desiredRevision":0,"publishedRevision":null,"lastProblemCode":null},"status":{"supported":true,"installed":false,"running":false,"enabled":false,"managed":false,"configurationRevision":"","packageManagementSupported":true,"recoveryRequired":false},"matchesPublishedConfiguration":false}""")
        assertTrue(SquidWire.snapshot(payload.toString()).status.packageManagementSupported)
        val status = payload.getJSONObject("status")
        status.put("packageManagementSupported", false)
        val unsupported = SquidWire.snapshot(payload.toString()).status
        assertTrue(unsupported.supported)
        assertFalse(unsupported.packageManagementSupported)
        status.remove("packageManagementSupported")
        assertTrue(runCatching { SquidWire.snapshot(payload.toString()) }.isFailure)
    }
}
