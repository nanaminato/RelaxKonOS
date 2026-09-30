package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class SmbWireTest {
    private val share = """{"id":"共有 A/../b","name":"共有 A","path":"/srv/data","description":null,"readOnly":true,"enabled":false,"guestAllowed":false,"permissions":[{"principal":"alice","access":"readWrite"}],"managed":false,"drifted":true}"""
    @Test fun `current camelCase contracts preserve ownership drift and permissions`() {
        val value = SmbWire.shares("[$share]").single()
        assertFalse(value.managed); assertTrue(value.drifted); assertEquals(SmbAccess.ReadWrite, value.permissions.single().access)
        assertTrue(value.readOnly); assertFalse(value.enabled); assertNull(value.description)
        val status = SmbWire.status("""{"protocol":"smb","state":"running","version":null,"serviceActive":true,"port445Listening":false,"healthProblemCode":"file-services.smb.port_unavailable"}""")
        assertTrue(status.serviceActive); assertFalse(status.port445Listening)
        assertEquals(SmbRuntimeState.Running, status.state)
    }
    @Test fun `opaque Windows share names stay one encoded route segment`() {
        assertEquals(SmbRoutes.ROOT + "/shares/%E5%85%B1%E6%9C%89%20A%2F..%2Fb", SmbRoutes.share("共有 A/../b"))
        assertEquals(SmbRoutes.ROOT + "/users/alice%40host/password", SmbRoutes.route(SmbChange(SmbChangeKind.Password, "alice@host")))
        assertTrue(runCatching { SmbRoutes.share("bad\nname") }.isFailure)
    }
    @Test fun `invalid or ambiguous reads never become empty verified facts`() {
        assertTrue(runCatching { SmbWire.shares("[$share,$share]") }.isFailure)
        assertTrue(runCatching { SmbWire.shares("[$share]".replace("readWrite", "ReadWrite")) }.isFailure)
        assertTrue(runCatching { SmbWire.shares("{}") }.isFailure)
        assertTrue(runCatching { SmbWire.status("""{"protocol":"smb","state":"Running"}""") }.isFailure)
        assertTrue(runCatching { SmbWire.receipt("""{"operationId":"random","succeeded":true,"problemCode":null}""") }.isFailure)
        assertTrue(runCatching { SmbWire.users("""[{"username":"alice","enabled":true,"eligible":true},{"username":"alice","enabled":false,"eligible":true}]""") }.isFailure)
        assertEquals(emptyList<SmbShare>(), SmbWire.shares("[]"))
    }
    @Test fun `replacement includes every share field and current access spelling`() {
        val body = JSONObject(SmbShareRequest("共有", "/srv/data", null, true, false, true, listOf(SmbPermission("alice", SmbAccess.ReadWrite))).body().toByteArray().decodeToString())
        assertTrue(body.isNull("description")); assertTrue(body.getBoolean("guestAllowed"))
        assertTrue(body.getBoolean("readOnly")); assertFalse(body.getBoolean("enabled"))
        assertEquals("readWrite", body.getJSONArray("permissions").getJSONObject(0).getString("access"))
    }
}
