package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import java.io.*
import java.util.UUID

data class PendingSmbMutation(val serviceId: String, val account: String, val kind: SmbChangeKind, val target: String?, val markerId: String, val receiptId: String? = null)
class FileSmbMutationStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "smb-mutations.bin")
    override fun read() = file.takeIf(File::isFile)?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs(); val temporary = File(directory, "smb-mutations.bin.tmp"); temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to persist SMB mutation") }
    }
}
/** No share body, path, password, or remote-state inference. Receipt IDs are audit correlation, not resumable tasks. */
class SmbMutationJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(owner: SessionState.Active, change: SmbChange): PendingSmbMutation {
        val entries = read(); require(entries.size < 100 && entries.none { it.serviceId == owner.serviceId && it.account == owner.userName })
        change.target?.let(SmbRoutes::segment)
        return PendingSmbMutation(owner.serviceId, owner.userName, change.kind, change.target, UUID.randomUUID().toString()).also { write(entries + it) }
    }
    @Synchronized fun receipt(entry: PendingSmbMutation, id: String) {
        val entries = read(); require(entries.any { it.markerId == entry.markerId })
        write(entries.map { if (it.markerId == entry.markerId) it.copy(receiptId = InstallationRoutes.canonicalId(id)) else it })
    }
    @Synchronized fun complete(entry: PendingSmbMutation) = write(read().filterNot { it.markerId == entry.markerId && it.serviceId == entry.serviceId && it.account == entry.account })
    private fun read(): List<PendingSmbMutation> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B5331); val count = input.readInt().also { require(it in 0..100) }
            List(count) { PendingSmbMutation(input.readUTF(), input.readUTF(), SmbChangeKind.valueOf(input.readUTF()), input.readUTF().takeIf(String::isNotEmpty),
                InstallationRoutes.canonicalId(input.readUTF()), input.readUTF().takeIf(String::isNotEmpty)?.let(InstallationRoutes::canonicalId)) }
                .also { require(input.available() == 0) }
        }
    }
    private fun write(entries: List<PendingSmbMutation>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { out ->
            out.writeInt(0x524B5331); out.writeInt(entries.size)
            entries.forEach { out.writeUTF(it.serviceId); out.writeUTF(it.account); out.writeUTF(it.kind.name); out.writeUTF(it.target.orEmpty()); out.writeUTF(it.markerId); out.writeUTF(it.receiptId.orEmpty()) }
        }; storage.write(buffer.toByteArray())
    }
}
