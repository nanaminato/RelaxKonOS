package app.relaxkonos.mobile.core.net

import org.junit.Assert.*
import org.junit.Test
import org.json.JSONObject

const val FRPS_JSON = """{"bindAddress":"127.0.0.1","bindPort":7000,"allowPorts":[{"start":6000,"end":6010}],"vhostHttpPort":null,"vhostHttpsPort":443,"forceTls":true,"tokenConfigured":true,"dashboardEnabled":true,"dashboardAddress":"127.0.0.1","dashboardPort":7500,"dashboardUser":"admin","dashboardPasswordConfigured":true,"state":"running","revision":3,"appliedRevision":2,"problemCode":"","startedAt":"2026-09-30T10:00:00Z","token":null}"""
class ManagedFrpsWireTest {
    @Test fun `configuration reads preserve credentials and saved applied revisions`() {
        val value = ManagedFrpsWire.configuration(FRPS_JSON)
        assertEquals(3L, value.revision); assertEquals(2L, value.appliedRevision)
        val secret = FRPS_JSON.replace("\"token\":null", "\"token\":\"private-token\"")
        assertEquals("private-token", ManagedFrpsWire.configuration(secret).token)
        val dashboard = JSONObject(secret).put("dashboardPassword", "private-dashboard").toString()
        assertEquals("private-dashboard", ManagedFrpsWire.configuration(dashboard).dashboardPassword)
        assertEquals("private-token", ManagedFrpsWire.editing(secret).token!!.concatToString()); assertFalse(ManagedFrpsWire.editing(secret).toString().contains("private-token"))
    }
    @Test fun `missing revision invalid ranges and impossible applied proof fail closed`() {
        for (json in listOf(JSONObject(FRPS_JSON).apply { remove("revision") }.toString(), JSONObject(FRPS_JSON).apply { remove("appliedRevision") }.toString(),
            FRPS_JSON.replace("\"end\":6010", "\"end\":5999"), FRPS_JSON.replace("\"appliedRevision\":2", "\"appliedRevision\":4"), FRPS_JSON.replace("\"running\"", "\"stopped\""))) {
            assertTrue(runCatching { ManagedFrpsWire.configuration(json) }.isFailure)
        }
    }
    @Test fun `request sends current CAS revision and preserves secrets with null replacement`() {
        val request = ManagedFrpsRequest(true, "::1", 7000, listOf(TunnelPortRange(6000, 6010)), null, null, true, null, false, "127.0.0.1", null, null, null, 3)
        val body = JSONObject(request.body().toByteArray().decodeToString())
        assertEquals(3L, body.getLong("expectedRevision")); assertTrue(body.isNull("token")); assertTrue(body.isNull("dashboardPassword"))
        assertEquals(6010, body.getJSONArray("allowPorts").getJSONObject(0).getInt("end"))
    }
    @Test fun `unknown process state is current while unknown enum and unbounded audit are refused`() {
        assertEquals(ManagedFrpsState.Unknown, ManagedFrpsWire.configuration(FRPS_JSON.replace("\"running\"", "\"unknown\"").replace("\"appliedRevision\":2", "\"appliedRevision\":null")).state)
        assertTrue(runCatching { ManagedFrpsWire.configuration(FRPS_JSON.replace("\"running\"", "\"healthy\"")) }.isFailure)
        val entry = """{"timestamp":"2026-09-30T10:00:00Z","action":"frps.configure","result":"failed","problemCode":"tunnel.revision_conflict"}"""
        assertEquals("frps.configure", ManagedFrpsWire.audit("[$entry]").single().action)
        assertTrue(runCatching { ManagedFrpsWire.audit(List(201) { entry }.joinToString(prefix = "[", postfix = "]")) }.isFailure)
    }
}
