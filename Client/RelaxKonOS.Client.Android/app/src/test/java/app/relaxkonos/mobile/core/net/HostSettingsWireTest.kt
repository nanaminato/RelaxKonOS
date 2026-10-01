package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class HostSettingsWireTest {
    private val revision = "A".repeat(64)
    private val target = """{"resourceId":"host/time","scope":"hostMachine","platformIdentity":null}"""
    private val operation = """{"operationId":"ca57b835-8242-47bb-8a2b-c3a5f4658a6b","settingId":"host.time.zone","target":$target,"state":"applied","updatedAt":"2026-10-01T00:00:00Z","observedRevision":"$revision","problemCode":null,"effectiveState":"immediate"}"""
    @Test fun `current camelCase enums and explicit null fields are required`() {
        assertTrue(HostSettingsWire.operation(operation).terminal)
        assertTrue(runCatching { HostSettingsWire.operation(operation.replace("\"observedRevision\":\"$revision\"", "\"observedRevision\":null")) }.isFailure)
        assertTrue(runCatching { HostSettingsWire.operation(operation.replace("applied","Applied")) }.isFailure)
        val j = JSONObject(operation); j.remove("problemCode")
        assertTrue(runCatching { HostSettingsWire.operation(j.toString()) }.isFailure)
    }
    @Test fun `masked environment cannot leak values and malformed revisions are refused`() {
        val json = """{"target":{"resourceId":"host/environment/machine","scope":"hostMachine","platformIdentity":null},"revision":"$revision","observedAt":"2026-10-01T00:00:00Z","capability":{"state":"available","reasonCode":null},"effectiveState":"newLogin","provider":"linux-pam-environment","caseSensitiveNames":true,"pathSeparator":":","variables":[{"name":"PASSWORD","rawValue":null,"expandedPreview":null,"valueKind":"string","source":"hostMachine","sensitive":true,"masked":true,"warnings":[]}]}"""
        assertNull(HostSettingsWire.environment(json).variables.single().rawValue)
        assertTrue(runCatching { HostSettingsWire.environment(json.replace("\"rawValue\":null", "\"rawValue\":\"secret\"")) }.isFailure)
        assertTrue(runCatching { HostSettingsWire.operation(operation.replace(revision,"old")) }.isFailure)
    }
    @Test fun `hostname limits are remote and environment empty delete and expansion remain distinct`() {
        assertTrue(HostSettingsRules.hostname("long-hostname-001",63)); assertFalse(HostSettingsRules.hostname("long-hostname-001",15))
        assertFalse(HostSettingsRules.hostname("123",63)); assertFalse(HostSettingsRules.hostname("host.example",63))
        assertTrue(HostSettingsRules.mutation(HostEnvironmentMutation("X",false,""),false))
        assertFalse(HostSettingsRules.mutation(HostEnvironmentMutation("X",true,""),false))
        assertTrue(HostSettingsRules.mutation(HostEnvironmentMutation("X",true,null),false))
        assertFalse(HostSettingsRules.mutation(HostEnvironmentMutation("X",false,"%PATH%",true),false))
        assertTrue(HostSettingsRules.mutation(HostEnvironmentMutation("X",false,"%PATH%",true),true))
        assertFalse(HostSettingsRules.mutation(HostEnvironmentMutation("X",false,"\uD800"),true))
    }
    @Test fun `host high impact names use host case policy and operation ids cannot escape routes`() {
        assertTrue(HostSettingsRules.highImpact("path",true)); assertFalse(HostSettingsRules.highImpact("path",false))
        assertTrue(HostSettingsRules.highImpact("LD_PRELOAD",false)); assertTrue(HostSettingsRules.highImpact("DOTNET_FUTURE",true))
        assertTrue(runCatching { HostSettingsRoutes.operation("../other") }.isFailure)
    }
}
