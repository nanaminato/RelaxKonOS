package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import java.io.*
import java.util.UUID

/** No paths or configuration: enough to retain one explicitly confirmed lifecycle intent. */
data class PendingWebServerRequest(val serviceId: String, val account: String, val target: String,
    val action: String, val key: String, val operationId: String? = null, val attempted: Boolean = false)

class FileWebServerRequestStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "webserver-requests.bin")
    override fun read(): ByteArray? = file.takeIf { it.isFile }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "webserver-requests.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to persist web-server request.") }
    }
}

class WebServerRequestJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(owner: SessionState.Active, target: String, action: String): PendingWebServerRequest {
        val entries = read()
        val existing = entries.firstOrNull { it.serviceId == owner.serviceId && it.account == owner.userName && it.target == target }
        if (existing != null) {
            require(existing.action == action) { "Resolve the original web-server request first." }
            return existing
        }
        require(entries.size < 100)
        return PendingWebServerRequest(owner.serviceId, owner.userName, target, action, UUID.randomUUID().toString())
            .also { write(entries + it) }
    }
    @Synchronized fun update(entry: PendingWebServerRequest) {
        val entries = read()
        require(entries.any { same(it, entry) })
        write(entries.map { if (same(it, entry)) entry else it })
    }
    @Synchronized fun complete(entry: PendingWebServerRequest) = write(read().filterNot { same(it, entry) })
    private fun same(a: PendingWebServerRequest, b: PendingWebServerRequest) = a.serviceId == b.serviceId && a.account == b.account && a.key == b.key
    private fun read(): List<PendingWebServerRequest> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B5731)
            val count = input.readInt().also { require(it in 0..100) }
            List(count) { PendingWebServerRequest(input.readUTF(), input.readUTF(), input.readUTF(), input.readUTF(), input.readUTF(), input.readUTF().takeIf(String::isNotEmpty), input.readBoolean()) }
        }
    }
    private fun write(entries: List<PendingWebServerRequest>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(0x524B5731); output.writeInt(entries.size)
            entries.forEach {
                output.writeUTF(it.serviceId); output.writeUTF(it.account); output.writeUTF(it.target)
                output.writeUTF(it.action); output.writeUTF(it.key); output.writeUTF(it.operationId.orEmpty()); output.writeBoolean(it.attempted)
            }
        }
        storage.write(buffer.toByteArray())
    }
}
