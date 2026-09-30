package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.FirewallChangeKind
import java.io.*

data class PendingFirewallChange(val serviceId: String, val account: String, val kind: FirewallChangeKind, val number: Int?)
class FileFirewallRequestStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "firewall-requests.bin")
    override fun read() = file.takeIf(File::isFile)?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "firewall-requests.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to persist firewall request") }
    }
}
/** Unknown synchronous changes have no operation ID or safe replay key. Never persist the password or rule body. */
class FirewallMutationJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(owner: SessionState.Active, kind: FirewallChangeKind, number: Int?): PendingFirewallChange {
        val entries = read()
        require(entries.none { it.serviceId == owner.serviceId && it.account == owner.userName })
        require(entries.size < 100)
        return PendingFirewallChange(owner.serviceId, owner.userName, kind, number).also { write(entries + it) }
    }
    @Synchronized fun complete(entry: PendingFirewallChange) = write(read().filterNot { it == entry })
    private fun read(): List<PendingFirewallChange> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B4631)
            val count = input.readInt().also { require(it in 0..100) }
            List(count) { PendingFirewallChange(input.readUTF(), input.readUTF(), FirewallChangeKind.valueOf(input.readUTF()), input.readInt().takeIf { it != 0 }) }
                .also { require(input.available() == 0) }
        }
    }
    private fun write(entries: List<PendingFirewallChange>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { out ->
            out.writeInt(0x524B4631); out.writeInt(entries.size)
            entries.forEach { out.writeUTF(it.serviceId); out.writeUTF(it.account); out.writeUTF(it.kind.name); out.writeInt(it.number ?: 0) }
        }
        storage.write(buffer.toByteArray())
    }
}
