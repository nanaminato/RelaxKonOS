package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.nio.file.StandardCopyOption

enum class DeploymentControlKind { Rollback, Delete, Start, Stop, Restart, Cancel }
data class PendingDeploymentControl(val serviceId: String, val account: String, val applicationId: String,
    val kind: DeploymentControlKind, val argument: String, val key: String)

interface DeploymentControlStorage {
    fun read(): ByteArray?
    fun write(bytes: ByteArray)
}
class MemoryDeploymentControlStorage : DeploymentControlStorage {
    private var bytes: ByteArray? = null
    override fun read() = bytes?.copyOf()
    override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
}
class FileDeploymentControlStorage(private val directory: File) : DeploymentControlStorage {
    private val file get() = File(directory, "deployment-control-requests.json")
    override fun read(): ByteArray? {
        if (!file.exists()) return null
        require(file.length() <= 262_144)
        return file.readBytes()
    }
    override fun write(bytes: ByteArray) {
        check(directory.isDirectory || directory.mkdirs())
        val temporary = File(directory, "deployment-control-requests.json.tmp")
        FileOutputStream(temporary).use { it.write(bytes); it.fd.sync() }
        Files.move(temporary.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
    }
}

/** Only references and exact request keys; unreadable storage fails closed, never inventing a new key. */
class DeploymentControlJournal(private val storage: DeploymentControlStorage) {
    @Synchronized fun pending(owner: SessionState.Active): List<PendingDeploymentControl> =
        entries().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun pending(owner: SessionState.Active, applicationId: String): PendingDeploymentControl? =
        pending(owner).singleOrNull { it.applicationId == applicationId }

    @Synchronized fun begin(request: PendingDeploymentControl) {
        validate(request)
        val entries = entries()
        check(entries.none { it.serviceId == request.serviceId && it.account == request.account && it.applicationId == request.applicationId })
        check(entries.size < 100) // Unresolved requests must never be evicted to make room.
        write(entries + request)
    }

    @Synchronized fun complete(request: PendingDeploymentControl) { write(entries().filterNot { it == request }) }

    private fun entries(): List<PendingDeploymentControl> {
        val bytes = storage.read() ?: return emptyList()
        require(bytes.size <= 262_144)
        val root = JSONObject(Charsets.UTF_8.newDecoder().decode(java.nio.ByteBuffer.wrap(bytes)).toString())
        require(root.getInt("version") == 1)
        val array = root.getJSONArray("requests")
        require(array.length() <= 100)
        val result = (0 until array.length()).map { index ->
            val item = array.getJSONObject(index)
            PendingDeploymentControl(item.getString("serviceId"), item.getString("account"), item.getString("applicationId"),
                DeploymentControlKind.valueOf(item.getString("kind")), item.getString("argument"), item.getString("key")).also(::validate)
        }
        require(result.distinctBy { Triple(it.serviceId, it.account, it.applicationId) }.size == result.size)
        return result
    }
    private fun validate(request: PendingDeploymentControl) {
        require(request.serviceId.length in 1..128 && request.account.length in 1..256 && request.applicationId.length in 1..128 &&
            request.argument.length <= 128 && request.key.length in 1..128 && request.key.all { it.code in 33..126 })
        require(when (request.kind) {
            DeploymentControlKind.Rollback, DeploymentControlKind.Cancel -> request.argument.isNotBlank()
            else -> request.argument.isEmpty()
        })
    }
    private fun write(entries: List<PendingDeploymentControl>) {
        val array = JSONArray()
        entries.forEach { array.put(JSONObject().put("serviceId", it.serviceId).put("account", it.account)
            .put("applicationId", it.applicationId).put("kind", it.kind.name).put("argument", it.argument).put("key", it.key)) }
        val bytes = JSONObject().put("version", 1).put("requests", array).toString().toByteArray(Charsets.UTF_8)
        require(bytes.size <= 262_144)
        storage.write(bytes)
    }
}
