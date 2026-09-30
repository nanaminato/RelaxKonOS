package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.CertificateAction
import java.io.*
import java.util.UUID

/** Only actor/target/action, digest, original request key and known IDs. No SAN, email, PEM or secrets. */
data class PendingCertificateRequest(val serviceId: String, val account: String, val target: String?,
    val action: CertificateAction, val digest: String, val key: String, val operationId: String? = null, val attempted: Boolean = false)
class FileCertificateRequestStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "certificate-requests.bin")
    override fun read() = file.takeIf { it.isFile }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "certificate-requests.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to persist certificate request.") }
    }
}
internal class CertificateOriginalRequestPending : IllegalArgumentException()
class CertificateRequestJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(owner: SessionState.Active, target: String?, action: CertificateAction, digest: String): PendingCertificateRequest {
        val entries = read()
        val original = entries.firstOrNull { it.serviceId == owner.serviceId && it.account == owner.userName && it.target == target }
        if (original != null) { if (original.action != action || original.digest != digest) throw CertificateOriginalRequestPending(); return original }
        require(entries.size < 100)
        return PendingCertificateRequest(owner.serviceId, owner.userName, target, action, digest, UUID.randomUUID().toString()).also { write(entries + it) }
    }
    @Synchronized fun update(entry: PendingCertificateRequest) {
        val entries = read(); require(entries.any { same(it, entry) }); write(entries.map { if (same(it, entry)) entry else it })
    }
    @Synchronized fun complete(entry: PendingCertificateRequest) = write(read().filterNot { same(it, entry) })
    private fun same(a: PendingCertificateRequest, b: PendingCertificateRequest) = a.serviceId == b.serviceId && a.account == b.account && a.key == b.key
    private fun read(): List<PendingCertificateRequest> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B4331)
            val count = input.readInt().also { require(it in 0..100) }
            val result = List(count) { PendingCertificateRequest(input.readUTF(), input.readUTF(), input.readUTF().takeIf(String::isNotEmpty),
                CertificateAction.valueOf(input.readUTF()), input.readUTF(), input.readUTF(), input.readUTF().takeIf(String::isNotEmpty), input.readBoolean()) }
            require(input.available() == 0); result
        }
    }
    private fun write(entries: List<PendingCertificateRequest>) {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { output ->
            output.writeInt(0x524B4331); output.writeInt(entries.size)
            entries.forEach { output.writeUTF(it.serviceId); output.writeUTF(it.account); output.writeUTF(it.target.orEmpty()); output.writeUTF(it.action.name)
                output.writeUTF(it.digest); output.writeUTF(it.key); output.writeUTF(it.operationId.orEmpty()); output.writeBoolean(it.attempted) }
        }
        storage.write(bytes.toByteArray())
    }
}
