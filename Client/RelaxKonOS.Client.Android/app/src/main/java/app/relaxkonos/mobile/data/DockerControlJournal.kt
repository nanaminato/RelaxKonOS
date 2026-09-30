package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import java.io.*
import java.util.UUID

data class PendingDockerControl(val serviceId: String, val account: String, val kind: DockerControlKind, val target: String?, val markerId: String)
class FileDockerControlStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "docker-control.bin")
    override fun read() = file.takeIf(File::isFile)?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs(); val temporary = File(directory, "docker-control.bin.tmp"); temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to persist Docker control marker") }
    }
}
/** Local uncertain-write records. IDs are never sent as idempotency keys and no payload is saved. */
class DockerControlJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(owner: SessionState.Active, change: DockerControlChange): PendingDockerControl {
        val entries = read(); require(entries.size < 100 && entries.none { it.serviceId == owner.serviceId && it.account == owner.userName })
        change.target?.let(InstallationRoutes::canonicalId)
        return PendingDockerControl(owner.serviceId, owner.userName, change.kind, change.target, UUID.randomUUID().toString()).also { write(entries + it) }
    }
    @Synchronized fun complete(entry: PendingDockerControl) = write(read().filterNot {
        it.markerId == entry.markerId && it.serviceId == entry.serviceId && it.account == entry.account
    })
    private fun read(): List<PendingDockerControl> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B4431)
            val count = input.readInt().also { require(it in 0..100) }
            List(count) { PendingDockerControl(input.readUTF(), input.readUTF(), DockerControlKind.valueOf(input.readUTF()),
                input.readUTF().takeIf(String::isNotEmpty)?.let(InstallationRoutes::canonicalId), InstallationRoutes.canonicalId(input.readUTF())) }
                .also { require(input.available() == 0) }
        }
    }
    private fun write(entries: List<PendingDockerControl>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(0x524B4431); output.writeInt(entries.size)
            entries.forEach { output.writeUTF(it.serviceId); output.writeUTF(it.account); output.writeUTF(it.kind.name); output.writeUTF(it.target.orEmpty()); output.writeUTF(it.markerId) }
        }
        storage.write(buffer.toByteArray())
    }
}
