package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

internal const val RUNTIME_KEY = "frozen-plan-key"
internal const val RUNTIME_PLAN = """{"runtimeId":"mihomo","request":{"installationId":"00112233-4455-6677-8899-aabbccddeeff","hostId":"host","change":"update","expectedStateRevision":3,"expectedCatalogRevision":4,"expectedTrustRevision":5,"releaseId":"release-b","idempotencyKey":"frozen-plan-key","allowPrerelease":false,"keepData":true},"release":{"runtimeId":"mihomo","releaseId":"release-b","artifact":{"sha256":"digest","layout":{"files":[{"path":"mihomo","length":42}]}}},"previousReleaseId":"release-a"}"""
class ManagedRuntimeWireTest {
    @Test fun `current snapshot explicitly reports unavailable or preserved commit evidence`() {
        assertTrue(runCatching { ManagedRuntimeInstallation("""{"installation":{}}""") }.isFailure)
        assertTrue(runCatching { ManagedRuntimeInstallation("""{"installation":{},"commitWitness":7}""") }.isFailure)
        assertEquals(null, ManagedRuntimeInstallation("""{"installation":{},"commitWitness":null}""").commitWitness)
        val evidence = """{"operationId":"00112233-4455-6677-8899-aabbccddeeff","installation":{"stateRevision":9},"configurationIdentity":null}"""
        val installation = ManagedRuntimeInstallation("""{"installation":{},"commitWitness":$evidence}""")
        assertEquals(9, JSONObject(installation.commitWitness!!).getJSONObject("installation").getInt("stateRevision"))
    }
    private val resourceNames = listOf("geoip.metadb", "geoip.dat", "geosite.dat", "country.mmdb", "GeoLite2-ASN.mmdb")
    private fun resourceSnapshot(state: String = "verified"): JSONObject = JSONObject().put("revisionId", "00112233-4455-6677-8899-aabbccddeeff")
        .put("files", org.json.JSONArray(resourceNames.map { name -> JSONObject().put("name", name).put("state", state).apply {
            if (state == "verified") { put("length", 42); put("sha256", "a".repeat(64)) }
        } }))

    @Test fun `resource reads distinguish missing from verified and reject incomplete evidence`() {
        val missing = ManagedRuntimeResourceWire.snapshot(resourceSnapshot("missing").toString())
        assertTrue(missing.canPrepare); assertFalse(missing.verified)
        val healthy = resourceSnapshot()
        assertTrue(ManagedRuntimeResourceWire.snapshot(healthy.toString()).verified)
        val duplicate = resourceSnapshot().apply { getJSONArray("files").getJSONObject(1).put("name", resourceNames[0]) }
        val unknownWithDigest = resourceSnapshot().apply { getJSONArray("files").getJSONObject(0).put("state", "unknown").put("problemCode", "runtime.resource_unreadable") }
        val missingWithDigest = resourceSnapshot().apply { getJSONArray("files").getJSONObject(0).put("state", "missing") }
        val unmeasured = resourceSnapshot().put("revisionId", JSONObject.NULL)
        for (invalid in listOf(duplicate, unknownWithDigest, missingWithDigest, unmeasured))
            assertTrue(runCatching { ManagedRuntimeResourceWire.snapshot(invalid.toString()) }.isFailure)
    }

    @Test fun `preparation succeeds only when its report matches a fresh complete observation`() {
        val observed = ManagedRuntimeResourceWire.snapshot(resourceSnapshot().toString())
        val report = resourceSnapshot()
        assertTrue(ManagedRuntimeResourceWire.matchesReport(report.toString(), observed))
        assertFalse(ManagedRuntimeResourceWire.matchesReport(report.put("revisionId", "11112233-4455-6677-8899-aabbccddeeff").toString(), observed))
        assertFalse(ManagedRuntimeResourceWire.matchesReport(resourceSnapshot().apply { getJSONArray("files").getJSONObject(0).put("sha256", "b".repeat(64)) }.toString(), observed))
        assertFalse(ManagedRuntimeResourceWire.matchesReport(resourceSnapshot().toString(), ManagedRuntimeResourceWire.snapshot(resourceSnapshot("missing").toString())))
    }

    @Test fun `frozen artifact and plan key survive installation serialization`() {
        val request = ManagedRuntimeInstallationRequest(true, RUNTIME_PLAN)
        val body = JSONObject(InstallationWire.request(request, InstallationKind.Upgrade).toByteArray().decodeToString())
        assertEquals(RUNTIME_KEY, request.key)
        assertEquals(setOf("confirmed", "plan"), body.keys().asSequence().toSet())
        assertEquals(42, body.getJSONObject("plan").getJSONObject("release").getJSONObject("artifact").getJSONObject("layout").getJSONArray("files").getJSONObject(0).getInt("length"))
    }
    @Test fun `cross runtime mismatched artifact and action cannot enter ledger`() {
        listOf(RUNTIME_PLAN.replace("mihomo", "unknown-runtime"), RUNTIME_PLAN.replaceFirst("mihomo", "sing-box"), RUNTIME_PLAN.replace("\"releaseId\":\"release-b\",\"artifact", "\"releaseId\":\"other\",\"artifact"), RUNTIME_PLAN.replace("update", "configure"))
            .forEach { assertTrue(runCatching { ManagedRuntimeInstallationRequest(true, it) }.isFailure) }
        assertTrue(runCatching { InstallationWire.request(ManagedRuntimeInstallationRequest(true, RUNTIME_PLAN), InstallationKind.Install) }.isFailure)
        assertFalse(InstallationService.Mihomo.acceptsPackage)
    }
    @Test fun `repair and rollback only select the host stored eligible release`() {
        val installation = ManagedRuntimeInstallation("""{"installation":{"activeReleaseId":"active"},"installedReleases":[{"releaseId":"old"},{"releaseId":"active"}],"commitWitness":null}""")
        assertTrue(installation.eligible(InstallationKind.Repair, "active"))
        assertFalse(installation.eligible(InstallationKind.Repair, "old"))
        assertTrue(installation.eligible(InstallationKind.Rollback, "old"))
        assertFalse(installation.eligible(InstallationKind.Rollback, "active"))
        assertFalse(installation.eligible(InstallationKind.Rollback, "remote-only"))
    }
    @Test fun `source names cannot escape closed routes`() {
        listOf("../source", "source?x=1", "https://example.test", "", "a".repeat(129)).forEach {
            assertTrue(runCatching { ManagedRuntimeRoutes.catalog(it, false, false) }.isFailure)
        }
        assertEquals("/api/v1.0/managed-runtimes/sources/release-1/refresh?includePrerelease=false", ManagedRuntimeRoutes.catalog("release-1", true, false))
    }
    @Test fun `unknown external and unfinished host facts cannot create overwrite plan`() {
        val wire = """{"installation":{"installationId":"00112233-4455-6677-8899-aabbccddeeff","hostId":"host","ownership":"unknown","stateRevision":3,"activeReleaseId":null,"recoveryRequired":false},"pendingOperationId":null,"measuredVersion":null,"problemCode":null,"commitWitness":null}"""
        for (value in listOf(wire, wire.replace("unknown", "external"), wire.replace("unknown", "absent").replace("false", "true"))) {
            assertTrue(runCatching { ManagedRuntimeInstallation(value).request(InstallationKind.Install, null, "release-b", RUNTIME_KEY, false, true) }.isFailure)
        }
        val request = JSONObject(ManagedRuntimeInstallation(wire.replace("unknown", "managed")).request(InstallationKind.Uninstall, null, null, RUNTIME_KEY, false, false).toByteArray().decodeToString())
        assertTrue(request.isNull("releaseId")); assertFalse(request.getBoolean("keepData"))
        assertEquals(3, request.getLong("expectedStateRevision"))
    }
    @Test fun `sing-box frozen plans bind installation service and immutable artifact`() {
        val request = ManagedRuntimeInstallationRequest(true, RUNTIME_PLAN.replace("mihomo", "sing-box"))
        assertEquals(InstallationService.SingBox, request.service)
        assertEquals("/api/v1.0/installations/SingBox/Upgrade", InstallationRoutes.start(request.service, request.kind))
        val body = JSONObject(InstallationWire.request(request, request.kind).toByteArray().decodeToString())
        assertEquals(RUNTIME_KEY, body.getJSONObject("plan").getJSONObject("request").getString("idempotencyKey"))
        assertEquals("sing-box", body.getJSONObject("plan").getJSONObject("release").getString("runtimeId"))
    }
}
