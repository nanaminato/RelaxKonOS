package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.InstallationKind
import app.relaxkonos.mobile.core.net.InstallationService
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.util.UUID

/** No options, paths, package bytes or credentials: only recovery lookup keys and a request digest. */
data class PendingInstallationRequest(
    val serviceId: String, val account: String, val service: InstallationService, val kind: InstallationKind,
    val key: String, val fingerprint: String, val attempted: Boolean = false, val operationId: String? = null,
)

interface InstallationRequestStorage {
    fun read(): ByteArray?
    fun write(bytes: ByteArray)
}

class FileInstallationRequestStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "installation-requests.bin")
    override fun read(): ByteArray? = file.takeIf { it.isFile }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "installation-requests.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) {
            temporary.delete()
            error("Unable to persist installation request.")
        }
    }
}

/** Storage errors fail closed so an unknown submission never silently receives a fresh key. */
class InstallationRequestJournal(private val storage: InstallationRequestStorage) {
    @Synchronized
    fun pending(owner: SessionState.Active): List<PendingInstallationRequest> = read().filter {
        it.serviceId == owner.serviceId && it.account == owner.userName
    }

    @Synchronized
    fun begin(owner: SessionState.Active, service: InstallationService, kind: InstallationKind, fingerprint: String): PendingInstallationRequest {
        val entries = read()
        val existing = entries.firstOrNull { it.serviceId == owner.serviceId && it.account == owner.userName && it.service == service }
        if (existing != null) {
            require(existing.kind == kind && existing.fingerprint == fingerprint) { "Resolve the original installation request first." }
            return existing
        }
        require(entries.size < 100) { "Installation recovery journal is full." }
        return PendingInstallationRequest(owner.serviceId, owner.userName, service, kind, UUID.randomUUID().toString(), fingerprint)
            .also { write(entries + it) }
    }

    @Synchronized
    fun update(entry: PendingInstallationRequest) {
        val entries = read()
        require(entries.any { same(it, entry) })
        write(entries.map { if (same(it, entry)) entry else it })
    }

    @Synchronized
    fun complete(entry: PendingInstallationRequest) = write(read().filterNot { same(it, entry) })

    private fun same(a: PendingInstallationRequest, b: PendingInstallationRequest) =
        a.serviceId == b.serviceId && a.account == b.account && a.service == b.service && a.key == b.key

    private fun read(): List<PendingInstallationRequest> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B4931)
            val count = input.readInt()
            require(count in 0..100)
            List(count) {
                PendingInstallationRequest(input.readUTF(), input.readUTF(), InstallationService.valueOf(input.readUTF()),
                    InstallationKind.valueOf(input.readUTF()), input.readUTF(), input.readUTF(), input.readBoolean(),
                    input.readUTF().takeIf { it.isNotEmpty() })
            }
        }
    }

    private fun write(entries: List<PendingInstallationRequest>) {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { output ->
            output.writeInt(0x524B4931)
            output.writeInt(entries.size)
            entries.forEach {
                output.writeUTF(it.serviceId); output.writeUTF(it.account); output.writeUTF(it.service.name)
                output.writeUTF(it.kind.name); output.writeUTF(it.key); output.writeUTF(it.fingerprint)
                output.writeBoolean(it.attempted); output.writeUTF(it.operationId.orEmpty())
            }
        }
        storage.write(bytes.toByteArray())
    }
}
