package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import java.io.*
import java.util.UUID

data class PendingDockerResource(val serviceId: String, val account: String, val action: DockerResourceAction, val target: String?, val markerId: String)
class FileDockerResourceStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "docker-resources.bin")
    override fun read() = file.takeIf(File::isFile)?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs(); val temporary = File(directory, "docker-resources.bin.tmp"); temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to persist Docker resource marker") }
    }
}
class DockerResourceJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(owner: SessionState.Active, change: DockerResourceChange): PendingDockerResource {
        val entries = read(); require(entries.size < 100 && entries.none { it.serviceId == owner.serviceId && it.account == owner.userName })
        change.target?.let(DockerResourceRoutes::segment)
        return PendingDockerResource(owner.serviceId, owner.userName, change.action, change.target, UUID.randomUUID().toString()).also { write(entries + it) }
    }
    @Synchronized fun complete(marker: PendingDockerResource) = write(read().filterNot {
        it.markerId == marker.markerId && it.serviceId == marker.serviceId && it.account == marker.account
    })
    private fun read(): List<PendingDockerResource> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B4432); val count = input.readInt().also { require(it in 0..100) }
            List(count) { PendingDockerResource(input.readUTF(), input.readUTF(), DockerResourceAction.valueOf(input.readUTF()),
                input.readUTF().takeIf(String::isNotEmpty)?.also(DockerResourceRoutes::segment), InstallationRoutes.canonicalId(input.readUTF())) }
                .also { require(input.available() == 0) }
        }
    }
    private fun write(entries: List<PendingDockerResource>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { out ->
            out.writeInt(0x524B4432); out.writeInt(entries.size)
            entries.forEach { out.writeUTF(it.serviceId); out.writeUTF(it.account); out.writeUTF(it.action.name); out.writeUTF(it.target.orEmpty()); out.writeUTF(it.markerId) }
        }
        storage.write(buffer.toByteArray())
    }
}
