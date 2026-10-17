package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.WebsitePublishRequest
import java.io.*
import java.security.MessageDigest
import java.util.UUID

data class PendingWebsitePublication(val serviceId: String, val account: String, val applicationId: String,
    val serverId: String, val digest: String, val key: String, val attempted: Boolean = false, val operationId: String? = null)

class FileWebsitePublicationStorage(private val directory: File) : InstallationRequestStorage {
    override fun read(): ByteArray? = File(directory, "website-publications.bin").takeIf(File::isFile)?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val file = File(directory, "website-publications.bin")
        val temporary = File(directory, "website-publications.bin.tmp")
        temporary.writeBytes(bytes)
        check(temporary.renameTo(file)) { "Unable to persist website publication." }
    }
}

/** Stores identifiers and a request hash; domain, contact details and certificate contents stay out of the journal. */
class WebsitePublicationJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().singleOrNull { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(owner: SessionState.Active, request: WebsitePublishRequest): PendingWebsitePublication {
        val entries = read()
        val digest = digest(request)
        val existing = entries.singleOrNull { it.serviceId == owner.serviceId && it.account == owner.userName }
        if (existing != null) {
            require(existing.digest == digest && existing.applicationId == request.applicationId && existing.serverId == request.webServerId)
            return existing
        }
        require(entries.size < 100)
        val entry = PendingWebsitePublication(owner.serviceId, owner.userName, request.applicationId, request.webServerId, digest, UUID.randomUUID().toString())
        validate(entry); write(entries + entry)
        return entry
    }
    @Synchronized fun update(entry: PendingWebsitePublication) {
        validate(entry)
        val entries = read()
        require(entries.any { it.key == entry.key && it.serviceId == entry.serviceId && it.account == entry.account &&
            it.digest == entry.digest && it.applicationId == entry.applicationId && it.serverId == entry.serverId })
        write(entries.map { if (it.key == entry.key) entry else it })
    }
    @Synchronized fun complete(entry: PendingWebsitePublication) {
        val entries = read()
        require(entries.any { it == entry })
        write(entries.filterNot { it == entry })
    }
    fun matches(entry: PendingWebsitePublication, request: WebsitePublishRequest) = entry.applicationId == request.applicationId &&
        entry.serverId == request.webServerId && entry.digest == digest(request)
    private fun digest(request: WebsitePublishRequest): String {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { out ->
            out.writeUTF(request.applicationId); out.writeUTF(request.webServerId); out.writeUTF(request.domain)
            out.writeBoolean(request.certificateId != null); request.certificateId?.let(out::writeUTF)
            out.writeBoolean(request.contactEmail != null); request.contactEmail?.let(out::writeUTF)
            out.writeBoolean(request.acceptedTerms); out.writeBoolean(request.publicReachabilityConfirmed); out.writeBoolean(request.confirmed)
        }
        return MessageDigest.getInstance("SHA-256").digest(buffer.toByteArray()).joinToString("") { "%02x".format(it) }
    }
    private fun validate(entry: PendingWebsitePublication) {
        require(listOf(entry.serviceId, entry.account, entry.applicationId, entry.serverId).all { it.isNotBlank() && it.length <= 512 && it.none { c -> c.code < 32 } })
        require(entry.digest.matches(Regex("[0-9a-f]{64}")))
        require(UUID.fromString(entry.key).toString() == entry.key)
        require(entry.operationId == null || entry.attempted && entry.operationId.isNotBlank() && entry.operationId.length <= 512 && entry.operationId.none { it.code < 32 })
    }
    private fun read(): List<PendingWebsitePublication> {
        val bytes = storage.read() ?: return emptyList()
        require(bytes.size <= 256 * 1024)
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B5031)
            val count = input.readInt().also { require(it in 0..100) }
            val entries = List(count) { PendingWebsitePublication(input.readUTF(), input.readUTF(), input.readUTF(), input.readUTF(),
                input.readUTF(), input.readUTF(), input.readBoolean(), if (input.readBoolean()) input.readUTF() else null).also(::validate) }
            require(input.available() == 0)
            require(entries.map { it.serviceId to it.account }.distinct().size == entries.size)
            require(entries.map { it.key }.distinct().size == entries.size)
            entries
        }
    }
    private fun write(entries: List<PendingWebsitePublication>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { out ->
            out.writeInt(0x524B5031); out.writeInt(entries.size)
            entries.forEach { entry ->
                validate(entry)
                out.writeUTF(entry.serviceId); out.writeUTF(entry.account); out.writeUTF(entry.applicationId); out.writeUTF(entry.serverId)
                out.writeUTF(entry.digest); out.writeUTF(entry.key); out.writeBoolean(entry.attempted)
                out.writeBoolean(entry.operationId != null); entry.operationId?.let(out::writeUTF)
            }
        }
        require(buffer.size() <= 256 * 1024)
        storage.write(buffer.toByteArray())
    }
}
