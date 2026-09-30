package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

internal const val TUNNEL_PROFILE_ID = "17f5a44e-5526-410d-a6f8-abc012345678"
internal const val TUNNEL_ID = "27f5a44e-5526-410d-a6f8-abc012345678"
internal const val TUNNEL_PROFILE_JSON = """{"id":"17f5a44e-5526-410d-a6f8-abc012345678","name":"edge","host":"frps.example.test","port":7000,"authKind":"token","tokenConfigured":true,"tlsMode":"force","runtimeMode":"managed","externalExecutablePath":null,"revision":3,"createdAt":"2026-09-01T00:00:00Z","updatedAt":"2026-09-30T00:00:00Z"}"""
internal const val TUNNEL_JSON = """{"id":"27f5a44e-5526-410d-a6f8-abc012345678","serverProfileId":"17f5a44e-5526-410d-a6f8-abc012345678","name":"ssh","providerId":"frp","protocol":"tcp","localHost":"127.0.0.1","localPort":22,"remotePort":6000,"domain":null,"enabled":true,"encryption":true,"compression":false,"revision":2,"createdAt":"2026-09-01T00:00:00Z","updatedAt":"2026-09-30T00:00:00Z","state":"savedNotApplied","problemCode":"tunnel.definition_not_applied"}"""
internal const val TUNNEL_RUNTIME_JSON = """{"runtimeId":"frp","mode":"managed","state":"available","version":"v0.71.1","executablePath":"/opt/frp/frpc","problemCode":"","startedAt":null,"previousVersion":"v0.71.0","integrityVerified":true}"""
class TunnelWireTest {
    @Test fun `FRP rollback maps to repair and package sources only apply to install`() {
        val rollback = org.json.JSONObject(InstallationWire.request(FrpInstallationRequest(true, rollback = true), InstallationKind.Repair).toByteArray().decodeToString())
        assertTrue(rollback.getBoolean("rollback")); assertTrue(rollback.isNull("version"))
        assertTrue(runCatching { InstallationWire.request(FrpInstallationRequest(true, "v0.71.1", fileReferenceId = "ref"), InstallationKind.Upgrade) }.isFailure)
    }
    @Test fun `safe current records preserve revision configured flag and actual state`() {
        val profile = TunnelWire.profiles("[$TUNNEL_PROFILE_JSON]").single()
        assertEquals(3L, profile.revision); assertTrue(profile.tokenConfigured); assertEquals(TunnelTls.Force, profile.tls)
        val tunnel = TunnelWire.definitions("[$TUNNEL_JSON]").single()
        assertEquals(TunnelConnectionState.SavedNotApplied, tunnel.state); assertEquals(6000, tunnel.remotePort)
        assertEquals("tunnel.definition_not_applied", tunnel.problemCode)
        assertTrue(TunnelWire.runtime(TUNNEL_RUNTIME_JSON).integrityVerified)
    }
    @Test fun `unknown enums duplicate IDs and malformed revisions fail closed`() {
        assertTrue(runCatching { TunnelWire.profile(TUNNEL_PROFILE_JSON.replace("force", "invented")) }.isFailure)
        assertTrue(runCatching { TunnelWire.definitions("[$TUNNEL_JSON,$TUNNEL_JSON]") }.isFailure)
        assertTrue(runCatching { TunnelWire.profile(TUNNEL_PROFILE_JSON.replace("\"revision\":3", "\"revision\":0")) }.isFailure)
        assertTrue(runCatching { TunnelWire.result("""{"succeeded":true,"state":"invented","problemCode":""}""") }.isFailure)
    }
    @Test fun `typed requests contain revision and no embedded credentials or protocol leftovers`() {
        val profile = TunnelProfileRequest("edge", "frps.test", 7000, TunnelAuth.Token, TunnelTls.Default, TunnelRuntimeMode.Managed, null, 3)
        val body = JSONObject(profile.body().toByteArray().decodeToString())
        assertFalse(body.has("token")); assertEquals(3L, body.getLong("expectedRevision")); assertTrue(body.isNull("externalExecutablePath"))
        val http = TunnelDefinitionRequest(TUNNEL_PROFILE_ID, "web", TunnelProtocol.Http, "127.0.0.1", 8080, null, "app.example.test", true, false, false, 2)
        assertTrue(JSONObject(http.body().toByteArray().decodeToString()).isNull("remotePort"))
        assertEquals("/api/v1.0/tunnels/profiles/$TUNNEL_PROFILE_ID/apply", TunnelRoutes.apply(TUNNEL_PROFILE_ID))
        assertTrue(runCatching { TunnelRoutes.logs("bad/id") }.isFailure)
    }
}
