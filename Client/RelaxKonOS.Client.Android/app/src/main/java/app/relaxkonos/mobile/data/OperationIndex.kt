package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File

enum class OperationDomain { Deployment, Website, Compose, GitBuild, Script, Backup, Installation, WebServer, Certificate, Proxy }

/** Only lookup keys are persisted. The remote domain record owns state, progress and diagnostics. */
data class OperationReference(
    val serviceId: String,
    val account: String,
    val domain: OperationDomain,
    val resourceId: String,
    val operationId: String,
    val seenAtMillis: Long,
    val hidden: Boolean = false,
    val lastVerifiedAtMillis: Long? = null,
)

interface OperationIndexStorage {
    fun read(): ByteArray?
    fun write(bytes: ByteArray)
}

class FileOperationIndexStorage(private val directory: File) : OperationIndexStorage {
    private val file get() = File(directory, "operations.bin")
    override fun read(): ByteArray? = file.takeIf { it.isFile }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "operations.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) {
            temporary.delete()
            throw IllegalStateException("Unable to save operation references.")
        }
    }
}

class OperationIndex(private val storage: OperationIndexStorage) {
    @Synchronized
    fun forOwner(owner: SessionState.Active): List<OperationReference> =
        read().filter { it.serviceId == owner.serviceId && it.account == owner.userName && !it.hidden }
            .sortedByDescending { it.seenAtMillis }

    @Synchronized
    fun isHidden(owner: SessionState.Active, domain: OperationDomain, operationId: String): Boolean =
        read().any { it.serviceId == owner.serviceId && it.account == owner.userName &&
            it.domain == domain && it.operationId == operationId && it.hidden }

    @Synchronized
    fun record(owner: SessionState.Active, domain: OperationDomain, resourceId: String, operationId: String) {
        require(resourceId.length in 1..128 && operationId.length in 1..128)
        require(resourceId.none { it.code < 32 || it.code == 127 } && operationId.none { it.code < 32 || it.code == 127 })
        val current = read()
        if (current.any { it.serviceId == owner.serviceId && it.account == owner.userName &&
                it.domain == domain && it.operationId == operationId }) return
        val entry = OperationReference(owner.serviceId, owner.userName, domain, resourceId, operationId, System.currentTimeMillis())
        write((listOf(entry) + current).take(MAX_RECORDS))
    }

    @Synchronized
    fun hide(owner: SessionState.Active, reference: OperationReference) {
        if (reference.serviceId != owner.serviceId || reference.account != owner.userName) return
        val current = read()
        val updated = if (current.any { it.serviceId == owner.serviceId && it.account == owner.userName &&
                it.domain == reference.domain && it.operationId == reference.operationId }) {
            current.map { if (it.serviceId == owner.serviceId && it.account == owner.userName &&
                    it.domain == reference.domain && it.operationId == reference.operationId) it.copy(hidden = true) else it }
        } else listOf(reference.copy(hidden = true)) + current
        write(updated.take(MAX_RECORDS))
    }

    /** Explicit recovery may show a previously hidden reference after the host verified it. */
    @Synchronized
    fun reveal(owner: SessionState.Active, domain: OperationDomain, operationId: String) {
        write(read().map { if (it.serviceId == owner.serviceId && it.account == owner.userName &&
            it.domain == domain && it.operationId == operationId) it.copy(hidden = false) else it })
    }

    @Synchronized
    fun markVerified(owner: SessionState.Active, domain: OperationDomain, operationId: String, atMillis: Long) {
        val current = read()
        if (current.none { it.serviceId == owner.serviceId && it.account == owner.userName &&
                it.domain == domain && it.operationId == operationId }) return
        write(current.map { if (it.serviceId == owner.serviceId && it.account == owner.userName &&
                it.domain == domain && it.operationId == operationId) it.copy(lastVerifiedAtMillis = atMillis) else it })
    }

    private fun read(): List<OperationReference> {
        return try {
            val bytes = storage.read() ?: return emptyList()
            DataInputStream(ByteArrayInputStream(bytes)).use { input ->
                if (input.readInt() != MAGIC) return emptyList()
                val count = input.readInt()
                if (count !in 0..MAX_RECORDS) return emptyList()
                List(count) {
                    OperationReference(input.readUTF(), input.readUTF(), OperationDomain.valueOf(input.readUTF()),
                        input.readUTF(), input.readUTF(), input.readLong(), input.readBoolean(),
                        input.readLong().takeIf { it > 0L })
                }
            }
        } catch (_: Exception) {
            emptyList()
        }
    }

    private fun write(entries: List<OperationReference>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(MAGIC)
            output.writeInt(entries.size)
            entries.forEach {
                output.writeUTF(it.serviceId)
                output.writeUTF(it.account)
                output.writeUTF(it.domain.name)
                output.writeUTF(it.resourceId)
                output.writeUTF(it.operationId)
                output.writeLong(it.seenAtMillis)
                output.writeBoolean(it.hidden)
                output.writeLong(it.lastVerifiedAtMillis ?: 0L)
            }
        }
        storage.write(buffer.toByteArray())
    }

    private companion object {
        const val MAGIC = 0x524B4F33
        const val MAX_RECORDS = 200
    }
}
