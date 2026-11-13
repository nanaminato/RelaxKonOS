package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ModulePackagesWireTest {
    private fun plan(operation: String? = null): String = JSONObject().put("planId", "00000000-0000-0000-0000-000000000001")
        .put("moduleId", "cloud-storage").put("version", "0.2.9").put("sourceId", "official").put("publisherId", "relaxkonos")
        .put("artifactSha256", "a".repeat(64)).put("expiresAt", "2030-01-01T00:00:00Z").put("runtime", "module-host")
        .put("privileges", org.json.JSONArray()).put("appliedOperationId", operation ?: JSONObject.NULL).toString()
    @Test fun `package preview preserves target and applied operation`() {
        val parsed = ModulePackagesWire.plan(plan())
        assertEquals("cloud-storage", parsed.moduleId); assertNull(parsed.operationId)
        val id = "00000000-0000-0000-0000-000000000002"
        assertEquals(id, ModulePackagesWire.plan(plan(id)).operationId)
    }
    @Test fun `upload rejects empty oversized or malformed receipt`() {
        val valid = JSONObject().put("uploadId", "a".repeat(32)).put("bytes", 42).put("sha256", "b".repeat(64))
        assertEquals(42L, ModulePackagesWire.upload(valid.toString()).bytes)
        listOf(0L, (1L shl 30) + 1).forEach { assertTrue(runCatching { ModulePackagesWire.upload(valid.put("bytes", it).toString()) }.isFailure) }
        assertTrue(runCatching { ModulePackagesWire.upload(valid.put("bytes", 42).put("uploadId", "../escape").toString()) }.isFailure)
    }
    @Test fun `package routes cannot escape and invalid digest fails`() {
        assertTrue(runCatching { ModulePackagesWire.planRoute("../apply") }.isFailure)
        assertTrue(runCatching { ModulePackagesWire.plan(JSONObject(plan()).put("artifactSha256", "bad").toString()) }.isFailure)
    }
    @Test fun `revoked or incompatible store version cannot be selected`() {
        val item = JSONObject().put("sourceId", "official").put("releaseId", "v1").put("moduleId", "cloud-storage")
            .put("version", "0.2.9").put("rid", "linux-x64").put("archiveBytes", 42).put("ridCompatible", true)
            .put("hostApiCompatible", true).put("revoked", false).put("blockedBy", org.json.JSONArray())
        val page = JSONObject().put("items", org.json.JSONArray().put(item)).put("blockedBy", org.json.JSONArray()).put("nextOffset", JSONObject.NULL)
        assertTrue(ModulePackagesWire.store(page.toString()).items.single().blockers.isEmpty())
        item.put("revoked", true)
        assertFalse(ModulePackagesWire.store(page.toString()).items.single().blockers.isEmpty())
    }
    @Test fun `acceptance receipt is not a succeeded operation`() {
        val id = "00000000-0000-0000-0000-000000000002"
        val receipt = ModulePackagesWire.receipt("""{"operationId":"$id","record":{"moduleId":"cloud-storage","version":"0.2.9"}}""")
        assertEquals(id, receipt.id)
        val operations = ModulePackagesWire.operations("""{"items":[{"operationId":"$id","moduleId":"cloud-storage","version":"0.2.9","state":"AwaitingRestart","problemCode":null}]}""")
        assertEquals("AwaitingRestart", operations.single().state)
    }
}
