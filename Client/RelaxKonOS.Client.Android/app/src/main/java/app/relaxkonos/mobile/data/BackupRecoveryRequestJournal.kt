package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.util.UUID

/**
 * Persists only an owner-scoped idempotency key for a backup request whose HTTP result was lost.
 * The remote manifest remains authoritative; this journal never stores a backup key, plaintext,
 * credential, response body, or a claim that the request succeeded.
 */
data class PendingBackupRequest(val serviceId: String, val account: String, val applicationId: String, val idempotencyKey: String)

interface BackupRecoveryRequestStorage {
    fun read(): ByteArray?
    fun write(bytes: ByteArray)
}

class FileBackupRecoveryRequestStorage(private val directory: File) : BackupRecoveryRequestStorage {
    private val file get() = File(directory, "backup-requests.bin")
    override fun read(): ByteArray? = file.takeIf { it.isFile }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "backup-requests.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) {
            temporary.delete()
            throw IllegalStateException("Unable to save backup request reference.")
        }
    }
}

class BackupRecoveryRequestJournal(private val storage: BackupRecoveryRequestStorage) {
    @Synchronized
    fun begin(owner: SessionState.Active, applicationId: String): PendingBackupRequest {
        require(applicationId.isNotBlank() && applicationId.length <= 128)
        val current = read()
        return current.firstOrNull { it.serviceId == owner.serviceId && it.account == owner.userName && it.applicationId == applicationId }
            ?: PendingBackupRequest(owner.serviceId, owner.userName, applicationId, UUID.randomUUID().toString()).also { entry ->
                write((listOf(entry) + current).take(MAXIMUM_ENTRIES))
            }
    }

    @Synchronized
    fun pending(owner: SessionState.Active, applicationId: String): PendingBackupRequest? = read()
        .firstOrNull { it.serviceId == owner.serviceId && it.account == owner.userName && it.applicationId == applicationId }

    @Synchronized
    fun complete(owner: SessionState.Active, applicationId: String, idempotencyKey: String) {
        write(read().filterNot { it.serviceId == owner.serviceId && it.account == owner.userName &&
            it.applicationId == applicationId && it.idempotencyKey == idempotencyKey })
    }

    private fun read(): List<PendingBackupRequest> = try {
        val bytes = storage.read() ?: return emptyList()
        DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            if (input.readInt() != MAGIC) return emptyList()
            val count = input.readInt()
            if (count !in 0..MAXIMUM_ENTRIES) return emptyList()
            List(count) {
                PendingBackupRequest(input.readUTF(), input.readUTF(), input.readUTF(), input.readUTF()).also { entry ->
                    require(entry.serviceId.length in 1..128 && entry.account.length in 1..256 && entry.applicationId.length in 1..128
                        && entry.idempotencyKey.length in 1..128)
                }
            }
        }
    } catch (_: Exception) { emptyList() }

    private fun write(entries: List<PendingBackupRequest>) {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { output ->
            output.writeInt(MAGIC)
            output.writeInt(entries.size)
            entries.forEach { entry ->
                output.writeUTF(entry.serviceId)
                output.writeUTF(entry.account)
                output.writeUTF(entry.applicationId)
                output.writeUTF(entry.idempotencyKey)
            }
        }
        storage.write(bytes.toByteArray())
    }

    private companion object {
        const val MAGIC = 0x524B4231
        const val MAXIMUM_ENTRIES = 100
    }
}
