package app.relaxkonos.mobile.servercenter

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.util.UUID

/** Pre-login operation lookup keys. No SSH credential, package path or request body is persisted. */
data class ServerInstallOperationReference(
    val hostId: String,
    val hostKeyAlgorithm: String,
    val hostKeyFingerprint: String,
    val operationId: String,
    val platform: ServerHostPlatform,
    val seenAtMillis: Long,
    val lastVerifiedAtMillis: Long? = null,
)

interface ServerInstallOperationStorage {
    fun read(): ByteArray?
    fun write(bytes: ByteArray)
}

class FileServerInstallOperationStorage(private val directory: File) : ServerInstallOperationStorage {
    private val file get() = File(directory, "server-install-operations.bin")
    override fun read(): ByteArray? = file.takeIf { it.isFile && it.length() <= MAX_STORAGE_BYTES }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "server-install-operations.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) {
            temporary.delete()
            throw IllegalStateException("Unable to save server operation references.")
        }
    }

    private companion object {
        const val MAX_STORAGE_BYTES = 64 * 1024L
    }
}

class ServerInstallOperationIndex(
    private val storage: ServerInstallOperationStorage,
    private val hostKeys: ServerHostKeyTrustStore,
) {
    @Synchronized
    fun forget(reference: ServerInstallOperationReference) {
        write(read().filterNot { it.hostId == reference.hostId && it.hostKeyAlgorithm == reference.hostKeyAlgorithm &&
            it.hostKeyFingerprint == reference.hostKeyFingerprint && it.operationId == reference.operationId })
    }

    @Synchronized
    fun forgetHost(hostId: String) {
        require(ServerHostTargetRules.isHostId(hostId))
        val current = read()
        if (current.any { it.hostId == hostId }) write(current.filterNot { it.hostId == hostId })
    }

    @Synchronized
    fun forTrustedHost(target: ServerHostTarget, key: ServerCenterHostKeyObservation): List<ServerInstallOperationReference> {
        require(validTargetKey(target, key)) { "The observed key does not belong to the selected host." }
        return read().filter { it.hostId == target.hostId && it.hostKeyAlgorithm == key.algorithm &&
            it.hostKeyFingerprint == key.fingerprint }.sortedByDescending { it.seenAtMillis }
    }

    @Synchronized
    fun record(target: ServerHostTarget, key: ServerCenterHostKeyObservation, operationId: String,
        platform: ServerHostPlatform): ServerInstallOperationReference {
        require(validTargetKey(target, key) && validId(operationId))
        val id = operationId.lowercase(java.util.Locale.ROOT)
        val current = read()
        val existing = current.firstOrNull { it.hostId == target.hostId && it.hostKeyAlgorithm == key.algorithm &&
            it.hostKeyFingerprint == key.fingerprint && it.operationId == id }
        if (existing != null) {
            require(existing.platform == platform) { "A recovery reference cannot change host platform." }
            return existing
        }
        val reference = ServerInstallOperationReference(target.hostId, key.algorithm, key.fingerprint, id,
            platform, System.currentTimeMillis())
        write((listOf(reference) + current).take(MAX_RECORDS))
        return reference
    }

    @Synchronized
    fun markVerified(target: ServerHostTarget, reference: ServerInstallOperationReference,
        key: ServerCenterHostKeyObservation, atMillis: Long) {
        require(atMillis > 0 && validTargetKey(target, key) && reference.hostId == target.hostId &&
            reference.hostKeyAlgorithm == key.algorithm && reference.hostKeyFingerprint == key.fingerprint)
        val current = read()
        val identity: (ServerInstallOperationReference) -> Boolean = {
            it.hostId == reference.hostId && it.hostKeyAlgorithm == reference.hostKeyAlgorithm &&
                it.hostKeyFingerprint == reference.hostKeyFingerprint && it.operationId == reference.operationId
        }
        if (current.none(identity)) return
        write(current.map { if (identity(it)) it.copy(lastVerifiedAtMillis = atMillis) else it })
    }

    private fun read(): List<ServerInstallOperationReference> {
        return try {
            val bytes = storage.read() ?: return emptyList()
            if (bytes.size > MAX_STORAGE_BYTES) return emptyList()
            DataInputStream(ByteArrayInputStream(bytes)).use { input ->
                if (input.readInt() != MAGIC) return emptyList()
                val count = input.readInt()
                if (count !in 0..MAX_RECORDS) return emptyList()
                List(count) {
                    ServerInstallOperationReference(input.readUTF(), input.readUTF(), input.readUTF(), input.readUTF(),
                        ServerHostPlatform.valueOf(input.readUTF()), input.readLong(), input.readLong().takeIf { it != 0L })
                }.takeIf { entries ->
                    entries.all(::valid) && entries.distinctBy {
                        listOf(it.hostId, it.hostKeyAlgorithm, it.hostKeyFingerprint, it.operationId)
                    }.size == entries.size && input.available() == 0
                } ?: emptyList()
            }
        } catch (_: Exception) { emptyList() }
    }

    private fun write(entries: List<ServerInstallOperationReference>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(MAGIC)
            output.writeInt(entries.size)
            entries.forEach {
                output.writeUTF(it.hostId)
                output.writeUTF(it.hostKeyAlgorithm)
                output.writeUTF(it.hostKeyFingerprint)
                output.writeUTF(it.operationId)
                output.writeUTF(it.platform.name)
                output.writeLong(it.seenAtMillis)
                output.writeLong(it.lastVerifiedAtMillis ?: 0L)
            }
        }
        val bytes = buffer.toByteArray()
        check(bytes.size <= MAX_STORAGE_BYTES) { "Too many server operation references." }
        storage.write(bytes)
    }

    private fun valid(reference: ServerInstallOperationReference): Boolean =
        ServerHostTargetRules.isHostId(reference.hostId) && validAlgorithm(reference.hostKeyAlgorithm) &&
            ServerHostTrustRules.isFingerprint(reference.hostKeyFingerprint) && validId(reference.operationId) &&
            reference.seenAtMillis > 0 && (reference.lastVerifiedAtMillis == null || reference.lastVerifiedAtMillis > 0)

    private fun validTargetKey(target: ServerHostTarget, key: ServerCenterHostKeyObservation): Boolean =
        ServerHostTargetRules.isHostId(target.hostId) &&
            target.hostId == ServerHostTargetRules.hostId(target.sshHost, target.sshPort, target.sshUserName) &&
            ServerHostTrustRules.endpointKey(target.sshHost, target.sshPort) ==
                ServerHostTrustRules.endpointKey(key.host, key.port) &&
            validAlgorithm(key.algorithm) && ServerHostTrustRules.isFingerprint(key.fingerprint) &&
            hostKeys.evaluate(ServerCenterSshEndpoint.create(target.sshHost, target.sshPort, target.sshUserName), key) ==
                ServerHostKeyTrust.Trusted

    private fun validAlgorithm(value: String): Boolean = value.length in 1..64 &&
        value.none { it.code < 32 || it.code == 127 }

    private fun validId(id: String): Boolean = OPERATION_ID.matches(id) && UUID.fromString(id) != UUID(0, 0)

    private companion object {
        const val MAGIC = 0x524B4931
        const val MAX_RECORDS = 100
        const val MAX_STORAGE_BYTES = 64 * 1024
        val OPERATION_ID = Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
    }
}
