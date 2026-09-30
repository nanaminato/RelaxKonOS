package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import java.io.*

/** Lookup facts and a digest only. Site definitions, filesystem paths and certificate material are not persisted. */
data class PendingSiteMutation(val serviceId: String, val account: String, val serverId: String,
    val siteId: String, val delete: Boolean, val fingerprint: String, val expectedUpdatedAt: String?, val attempted: Boolean = false)
class FileSiteMutationStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "web-site-mutations.bin")
    override fun read() = file.takeIf { it.isFile }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "web-site-mutations.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to persist site mutation.") }
    }
}
class WebSiteMutationJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(entry: PendingSiteMutation): PendingSiteMutation {
        val entries = read()
        val original = entries.firstOrNull { same(it, entry) }
        if (original != null) { require(original.fingerprint == entry.fingerprint && original.delete == entry.delete); return original }
        require(entries.size < 100)
        write(entries + entry); return entry
    }
    @Synchronized fun attempted(entry: PendingSiteMutation) = write(read().map { if (same(it, entry)) it.copy(attempted = true) else it })
    @Synchronized fun complete(entry: PendingSiteMutation) = write(read().filterNot { same(it, entry) })
    private fun same(a: PendingSiteMutation, b: PendingSiteMutation) = a.serviceId == b.serviceId && a.account == b.account && a.serverId == b.serverId && a.siteId == b.siteId
    private fun read(): List<PendingSiteMutation> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B5331)
            val count = input.readInt().also { require(it in 0..100) }
            List(count) { PendingSiteMutation(input.readUTF(), input.readUTF(), input.readUTF(), input.readUTF(), input.readBoolean(),
                input.readUTF(), input.readUTF().takeIf(String::isNotEmpty), input.readBoolean()) }
        }
    }
    private fun write(entries: List<PendingSiteMutation>) {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { output ->
            output.writeInt(0x524B5331); output.writeInt(entries.size)
            entries.forEach { output.writeUTF(it.serviceId); output.writeUTF(it.account); output.writeUTF(it.serverId); output.writeUTF(it.siteId)
                output.writeBoolean(it.delete); output.writeUTF(it.fingerprint); output.writeUTF(it.expectedUpdatedAt.orEmpty()); output.writeBoolean(it.attempted) }
        }
        storage.write(bytes.toByteArray())
    }
}
