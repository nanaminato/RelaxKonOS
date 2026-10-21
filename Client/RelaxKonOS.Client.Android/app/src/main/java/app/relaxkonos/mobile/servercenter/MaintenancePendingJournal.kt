package app.relaxkonos.mobile.servercenter

import java.io.*
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.UUID

internal data class MaintenancePending(val reference: ServerInstallOperationReference, val kind: ServerDeploymentKind)

internal interface MaintenancePendingStorage {
    fun read(): ByteArray?
    fun write(bytes: ByteArray)
}

internal class FileMaintenancePendingStorage(private val directory: File) : MaintenancePendingStorage {
    private val file get() = File(directory, "server-maintenance-pending.bin")
    override fun read(): ByteArray? {
        if (!file.exists()) return null
        require(file.isFile && file.length() <= 65536) { "Invalid maintenance journal" }
        return file.readBytes().also { require(it.size <= 65536) }
    }
    override fun write(bytes: ByteArray) {
        require(bytes.size <= 65536)
        check(directory.isDirectory || directory.mkdirs())
        val temporary = File(directory, "server-maintenance-pending.bin.tmp")
        FileOutputStream(temporary).use { stream -> stream.write(bytes); stream.fd.sync() }
        Files.move(temporary.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
    }
}

/** Bounded local recovery metadata only. Invalid or unavailable storage must never imply no pending operation. */
internal class MaintenancePendingJournal(private val storage: MaintenancePendingStorage) {
    @Synchronized fun load(hostId: String): MaintenancePending? {
        require(ServerHostTargetRules.isHostId(hostId))
        return read().firstOrNull { it.reference.hostId == hostId }
    }
    @Synchronized fun record(pending: MaintenancePending) {
        require(valid(pending))
        val current = read()
        val previous = current.firstOrNull { it.reference.hostId == pending.reference.hostId }
        check(previous == null || previous == pending) { "An original operation still needs verification" }
        if (previous == pending) return
        write(current + pending)
    }
    @Synchronized fun resolve(pending: MaintenancePending) {
        val current = read()
        val previous = current.firstOrNull { it.reference.hostId == pending.reference.hostId }
        check(previous == null || previous == pending) { "The pending operation changed" }
        if (previous == pending) write(current.filterNot { it == pending })
    }
    private fun read(): List<MaintenancePending> {
        val bytes = storage.read() ?: return emptyList()
        require(bytes.size <= 65536)
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B4D31)
            val count = input.readInt(); require(count in 0..100)
            val values = List(count) {
                val reference = ServerInstallOperationReference(input.readUTF(), input.readUTF(), input.readUTF(), input.readUTF(),
                    ServerHostPlatform.valueOf(input.readUTF()), input.readLong(), input.readLong().takeIf { it != 0L })
                MaintenancePending(reference, ServerDeploymentKind.valueOf(input.readUTF()))
            }
            require(values.all(::valid) && values.distinctBy { it.reference.hostId }.size == values.size && input.available() == 0)
            values
        }
    }
    private fun write(values: List<MaintenancePending>) {
        require(values.size <= 100)
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(0x524B4D31); output.writeInt(values.size)
            values.forEach { pending ->
                val reference = pending.reference
                output.writeUTF(reference.hostId); output.writeUTF(reference.hostKeyAlgorithm); output.writeUTF(reference.hostKeyFingerprint)
                output.writeUTF(reference.operationId); output.writeUTF(reference.platform.name)
                output.writeLong(reference.seenAtMillis); output.writeLong(reference.lastVerifiedAtMillis ?: 0L)
                output.writeUTF(pending.kind.name)
            }
        }
        storage.write(buffer.toByteArray())
    }
    private fun valid(value: MaintenancePending): Boolean {
        val reference = value.reference
        return ServerHostTargetRules.isHostId(reference.hostId) && reference.hostKeyAlgorithm.length in 1..64 &&
            reference.hostKeyAlgorithm.none { it.code < 32 || it.code == 127 } && ServerHostTrustRules.isFingerprint(reference.hostKeyFingerprint) &&
            runCatching { UUID.fromString(reference.operationId).toString() == reference.operationId && UUID.fromString(reference.operationId) != UUID(0, 0) }.getOrDefault(false) &&
            reference.seenAtMillis > 0 && (reference.lastVerifiedAtMillis == null || reference.lastVerifiedAtMillis > 0) &&
            value.kind in setOf(ServerDeploymentKind.Repair, ServerDeploymentKind.Uninstall, ServerDeploymentKind.Configure, ServerDeploymentKind.Firewall)
    }
}
