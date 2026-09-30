package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ProxyAction
import java.io.*
import java.util.UUID

/** Never persists URLs, YAML, node names or credentials. One unresolved mutation per owner. */
enum class ProxyWrite { Profile, Activate, Delete, Configuration, Import, Selection, Routing, Settings, GeoData, Connection }
data class PendingProxyRequest(val serviceId: String, val account: String, val action: ProxyAction?, val target: String?, val key: String, val write: ProxyWrite?)
class FileProxyRequestStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "proxy-requests.bin")
    override fun read() = file.takeIf { it.isFile }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs(); val temporary = File(directory, "proxy-requests.bin.tmp"); temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to save proxy request marker") }
    }
}
class ProxyRequestJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(owner: SessionState.Active, action: ProxyAction?, target: String?, write: ProxyWrite? = null): PendingProxyRequest {
        require((action == null) != (write == null))
        val entries = read(); require(entries.none { it.serviceId == owner.serviceId && it.account == owner.userName &&
            (action != ProxyAction.EmergencyDisableTun || it.action == ProxyAction.EmergencyDisableTun) })
        require(entries.size < 100)
        val entry = PendingProxyRequest(owner.serviceId, owner.userName, action, target, UUID.randomUUID().toString(), write)
        write(entries + entry); return entry
    }
    @Synchronized fun complete(entry: PendingProxyRequest) = write(read().filterNot { it == entry })
    private fun read(): List<PendingProxyRequest> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B5031)
            List(input.readInt().also { require(it in 0..100) }) {
                PendingProxyRequest(input.readUTF(), input.readUTF(), input.readUTF().takeIf(String::isNotEmpty)?.let(ProxyAction::valueOf),
                    input.readUTF().takeIf(String::isNotEmpty), input.readUTF(), input.readUTF().takeIf(String::isNotEmpty)?.let(ProxyWrite::valueOf))
            }.also { require(input.available() == 0) }
        }
    }
    private fun write(entries: List<PendingProxyRequest>) {
        val buffer = ByteArrayOutputStream(); DataOutputStream(buffer).use { output ->
            output.writeInt(0x524B5031); output.writeInt(entries.size)
            entries.forEach { output.writeUTF(it.serviceId); output.writeUTF(it.account); output.writeUTF(it.action?.name.orEmpty()); output.writeUTF(it.target.orEmpty()); output.writeUTF(it.key); output.writeUTF(it.write?.name.orEmpty()) }
        }; storage.write(buffer.toByteArray())
    }
}
