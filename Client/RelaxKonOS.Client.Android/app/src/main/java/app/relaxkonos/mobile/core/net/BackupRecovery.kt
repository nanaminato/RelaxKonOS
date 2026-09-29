package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import org.json.JSONArray
import org.json.JSONObject

data class BackupManifest(val backupId: String, val operationId: String, val applicationId: String, val state: String,
    val keyId: String, val objects: List<BackupObject>, val problemCode: String?)
data class BackupObject(val kind: String, val reference: String, val consistencyMethod: String, val length: Long, val sha256: String)
data class BackupPreflight(val backupId: String, val sourceApplicationId: String, val suggestedApplicationName: String?,
    val createsNewInstance: Boolean, val canRestore: Boolean, val blockers: List<String>)

object BackupRecoveryRoutes {
    private const val ROOT = "/api/v1.0/backup-recovery"
    fun definitionBackup(applicationId: String) = "$ROOT/applications/${URLEncoder.encode(applicationId, StandardCharsets.UTF_8.name())}/definition-backups"
    fun definitionBackupRequest(applicationId: String) = "$ROOT/applications/${URLEncoder.encode(applicationId, StandardCharsets.UTF_8.name())}/definition-backup-request"
    fun backups(applicationId: String) = "$ROOT/applications/${URLEncoder.encode(applicationId, StandardCharsets.UTF_8.name())}/backups"
    fun backup(id: String) = "$ROOT/backups/${URLEncoder.encode(id, StandardCharsets.UTF_8.name())}"
    fun preflight(id: String) = "${backup(id)}/restore-preflight"
}

internal object BackupRecoveryWire {
    fun manifests(payload: String): List<BackupManifest> = JSONArray(payload).let { array -> (0 until array.length()).map { manifest(array.getJSONObject(it)) } }
    fun manifest(payload: String): BackupManifest = manifest(JSONObject(payload))
    fun preflight(payload: String): BackupPreflight = JSONObject(payload).let { json -> BackupPreflight(json.getString("backupId"),
        json.getString("sourceApplicationId"), if (json.isNull("suggestedApplicationName")) null else json.getString("suggestedApplicationName"),
        json.getBoolean("createsNewInstance"), json.getBoolean("canRestore"), json.getJSONArray("blockers").let { items -> (0 until items.length()).map(items::getString) }) }
    private fun manifest(json: JSONObject): BackupManifest {
        val objects = json.getJSONArray("objects").let { items ->
            (0 until items.length()).map { index ->
                items.getJSONObject(index).let { item ->
                    BackupObject(item.getString("kind"), item.getString("reference"), item.getString("consistencyMethod"),
                        item.getLong("length"), item.getString("sha256"))
                }
            }
        }
        return BackupManifest(json.getString("backupId"), json.getString("operationId"), json.getString("applicationId"),
            json.getString("state"), json.getString("keyId"), objects,
            if (json.isNull("problemCode")) null else json.getString("problemCode"))
    }
}
