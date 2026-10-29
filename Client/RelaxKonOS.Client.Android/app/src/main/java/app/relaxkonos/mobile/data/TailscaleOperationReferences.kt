package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.TailscaleWire
import java.io.*
import java.util.UUID
import android.system.Os
import android.system.OsConstants

interface TailscaleReferenceStorage : InstallationRequestStorage {
    fun <T> locked(action: () -> T): T
}
class FileTailscaleReferenceStorage(private val directory: File, private val name: String = "tailscale-operations",
    private val syncDirectory: (File) -> Unit = { folder ->
        val descriptor = Os.open(folder.absolutePath,OsConstants.O_RDONLY,0)
        try { check(OsConstants.S_ISDIR(Os.fstat(descriptor).st_mode)); Os.fsync(descriptor) } finally { Os.close(descriptor) }
    }) : TailscaleReferenceStorage {
    init { require(name in setOf("tailscale-operations","tailscale-login-operations")) }
    private val file get() = File(directory,"$name.bin")
    override fun <T> locked(action: () -> T): T {
        check(directory.isDirectory || directory.mkdirs())
        return RandomAccessFile(File(directory,"$name.lock"),"rw").use { lockFile ->
            val lease = lockFile.channel.tryLock() ?: throw IOException("Tailscale references busy")
            lease.use { action() }
        }
    }
    override fun read(): ByteArray? {
        if (!file.exists()) return null
        require(file.length() <= 262144)
        return file.readBytes()
    }
    override fun write(bytes: ByteArray) {
        val temporary = File(directory,"$name.${UUID.randomUUID()}.tmp")
        try {
            FileOutputStream(temporary).use { it.write(bytes); it.fd.sync() }
            if (!temporary.renameTo(file)) throw IOException("Tailscale references publication failed")
            syncDirectory(directory)
        } finally { temporary.delete() }
    }
}
enum class TailscaleReferencePhase { Known, Executing, Closing }
data class TailscaleReference(val host: String, val account: String, val workspace: String, val operationId: String,
    val phase: TailscaleReferencePhase) {
    fun belongsTo(owner: SessionState.Active) = host == owner.serviceId && account == owner.userName && workspace == owner.workspaceId
}
/** Only identity and unresolved-submission markers persist. Facts, configurations and grants never do. */
class TailscaleOperationReferences(private val storage: TailscaleReferenceStorage) {
    @Synchronized fun read(owner: SessionState.Active) = storage.locked { load().filter { it.belongsTo(owner) } }
    @Synchronized fun remember(owner: SessionState.Active, id: String) = storage.locked {
        TailscaleWire.id(id)
        val entries = load().toMutableList()
        if (entries.any { it.belongsTo(owner) && it.operationId == id }) return@locked
        if (entries.count { it.belongsTo(owner) } >= 30) {
            val old = entries.indexOfLast { it.belongsTo(owner) && it.phase == TailscaleReferencePhase.Known }
            require(old >= 0); entries.removeAt(old)
        }
        if (entries.size >= 256) {
            val old = entries.indexOfLast { it.phase == TailscaleReferencePhase.Known }
            require(old >= 0); entries.removeAt(old)
        }
        entries.add(0,TailscaleReference(owner.serviceId,owner.userName,owner.workspaceId,id,TailscaleReferencePhase.Known))
        save(entries)
    }
    @Synchronized fun mark(owner: SessionState.Active, id: String, phase: TailscaleReferencePhase) = storage.locked {
        val entries = load()
        require(entries.any { it.belongsTo(owner) && it.operationId == id })
        save(entries.map { if (it.belongsTo(owner) && it.operationId == id) it.copy(phase = phase) else it })
    }
    private fun load(): List<TailscaleReference> {
        val bytes = storage.read() ?: return emptyList()
        require(bytes.size <= 262144)
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B5431)
            val count = input.readInt().also { require(it in 0..256) }
            List(count) { TailscaleReference(input.readUTF(),input.readUTF(),input.readUTF(),input.readUTF().also(TailscaleWire::id),
                TailscaleReferencePhase.valueOf(input.readUTF())) }.also { entries ->
                require(input.available() == 0 && entries.all { it.host.isNotBlank() && it.account.isNotBlank() && it.workspace.isNotBlank() })
                require(entries.distinctBy { listOf(it.host,it.account,it.workspace,it.operationId) }.size == entries.size)
            }
        }
    }
    private fun save(entries: List<TailscaleReference>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(0x524B5431); output.writeInt(entries.size)
            entries.forEach { output.writeUTF(it.host); output.writeUTF(it.account); output.writeUTF(it.workspace); output.writeUTF(it.operationId); output.writeUTF(it.phase.name) }
        }
        storage.write(buffer.toByteArray())
    }
}
