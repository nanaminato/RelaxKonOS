package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import java.time.OffsetDateTime
import java.util.UUID

data class ModulePackageUpload(val id: String, val bytes: Long, val sha256: String)
data class ModulePackagePlan(val id: String, val moduleId: String, val version: String, val sourceId: String,
    val publisherId: String, val sha256: String, val expiresAt: String, val runtime: String,
    val privileges: List<String>, val operationId: String?, val raw: String)
data class ModulePackageOperation(val id: String, val moduleId: String, val version: String, val state: String, val problem: String?)
data class ModulePackageRecovery(val plan: ModulePackagePlan, val operation: ModulePackageOperation?, val problem: String? = null)
data class ModulePackageReceipt(val id: String, val moduleId: String, val version: String)
data class ModuleStoreItem(val sourceId: String, val releaseId: String, val moduleId: String, val version: String,
    val rid: String, val bytes: Long, val blockers: List<String>)
data class ModuleStorePage(val items: List<ModuleStoreItem>, val nextOffset: Int?, val blockers: List<String>)
object ModulePackagesWire {
    const val ROOT = "/api/v1.0/server/module-packages"
    const val STORE = "/api/v1.0/server/module-store"
    fun planRoute(id: String): String { require(UUID.fromString(id).toString() == id.lowercase()); return "$ROOT/plans/$id" }
    private fun digest(value: String): String { require(value.matches(Regex("[a-f0-9]{64}"))); return value }
    fun upload(payload: String): ModulePackageUpload = JSONObject(payload).let {
        val id = it.getString("uploadId"); require(id.matches(Regex("[a-f0-9]{32}")))
        val bytes = it.getLong("bytes"); require(bytes in 1..(1L shl 30))
        ModulePackageUpload(id, bytes, digest(it.getString("sha256")))
    }
    fun plan(payload: String): ModulePackagePlan = JSONObject(payload).let {
        val id = it.getString("planId"); planRoute(id)
        OffsetDateTime.parse(it.getString("expiresAt"))
        val privileges = it.getJSONArray("privileges"); require(privileges.length() <= 128)
        ModulePackagePlan(id, it.getString("moduleId"), it.getString("version"), it.getString("sourceId"),
            it.getString("publisherId"), digest(it.getString("artifactSha256")), it.getString("expiresAt"), it.getString("runtime"),
            (0 until privileges.length()).map { index -> privileges.getJSONObject(index).let { p ->
                "${p.getString("kind")}: ${p.getJSONArray("resources")} · ${p.getJSONArray("platforms")} · executable=${p.getBoolean("executable")}" } },
            if (it.isNull("appliedOperationId")) null else it.getString("appliedOperationId").also { value -> UUID.fromString(value) }, payload)
    }
    fun operations(payload: String): List<ModulePackageOperation> = JSONObject(payload).getJSONArray("items").let { list ->
        require(list.length() <= 10000)
        (0 until list.length()).map { index -> list.getJSONObject(index).let {
            val id = it.getString("operationId"); UUID.fromString(id)
            val state = it.getString("state"); require(state in setOf("Applying", "AwaitingRestart", "Succeeded", "Failed"))
            ModulePackageOperation(id, it.getString("moduleId"), it.getString("version"), state,
                if (it.isNull("problemCode")) null else it.getString("problemCode"))
        } }
    }
    fun receipt(payload: String): ModulePackageReceipt = JSONObject(payload).let {
        val id = it.getString("operationId"); UUID.fromString(id)
        val record = it.getJSONObject("record")
        ModulePackageReceipt(id, record.getString("moduleId"), record.getString("version"))
    }
    fun store(payload: String): ModuleStorePage = JSONObject(payload).let { page ->
        val items = page.getJSONArray("items"); require(items.length() <= 100)
        fun blockers(value: JSONObject): List<String> = value.getJSONArray("blockedBy").let { list ->
            require(list.length() <= 256); (0 until list.length()).map { list.getJSONObject(it).getString("problemCode") } }
        ModuleStorePage((0 until items.length()).map { index -> items.getJSONObject(index).let {
            val blocked = blockers(it).toMutableList()
            if (!it.getBoolean("ridCompatible") || !it.getBoolean("hostApiCompatible") || it.getBoolean("revoked")) blocked += "module-release-incompatible"
            ModuleStoreItem(it.getString("sourceId"), it.getString("releaseId"), it.getString("moduleId"), it.getString("version"),
                it.getString("rid"), it.getLong("archiveBytes"), blocked)
        } }, if (page.isNull("nextOffset")) null else page.getInt("nextOffset").also { require(it >= 0) }, blockers(page))
    }
}
