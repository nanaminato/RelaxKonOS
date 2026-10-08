package app.relaxkonos.mobile.servercenter

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.nio.file.NoSuchFileException
import java.nio.file.attribute.BasicFileAttributes
import java.util.UUID

/** Recovery identity only; credentials and installation request fields must never be stored here. */
data class PendingServerInstall(val reference: ServerInstallOperationReference, val mode: ServerInstallMode,
    val kind: ServerDeploymentKind, val needsSudo: Boolean, val attempted: Boolean = false)

class PendingServerInstallStore(private val directory: File) {
    private fun file(hostId: String): File {
        require(ServerHostTargetRules.isHostId(hostId))
        return File(directory, "pending-server-install-$hostId.bin")
    }

    @Synchronized fun read(hostId: String): PendingServerInstall? {
        val file = file(hostId)
        val attributes = try { Files.readAttributes(file.toPath(), BasicFileAttributes::class.java) }
            catch (_: NoSuchFileException) { return null }
        require(attributes.isRegularFile && attributes.size() in 1..8192) { "Invalid pending installation record" }
        return DataInputStream(ByteArrayInputStream(file.readBytes())).use { input ->
            require(input.readInt() == MAGIC)
            val reference = ServerInstallOperationReference(input.readUTF(), input.readUTF(), input.readUTF(),
                input.readUTF(), ServerHostPlatform.valueOf(input.readUTF()), input.readLong())
            val pending = PendingServerInstall(reference, ServerInstallMode.valueOf(input.readUTF()),
                ServerDeploymentKind.valueOf(input.readUTF()), input.readBoolean(), input.readBoolean())
            require(reference.hostId == hostId && input.available() == 0)
            validate(pending)
            pending
        }
    }

    @Synchronized fun write(pending: PendingServerInstall) {
        validate(pending)
        require(!pending.attempted) { "Reserve the operation before marking its dispatch" }
        val current = read(pending.reference.hostId)
        require(current == null || current == pending) { "An earlier installation still requires verification" }
        persist(pending)
    }

    @Synchronized fun markStarted(pending: PendingServerInstall): PendingServerInstall {
        require(!pending.attempted && read(pending.reference.hostId) == pending)
        return pending.copy(attempted = true).also(::persist)
    }

    private fun persist(pending: PendingServerInstall) {
        val bytes = ByteArrayOutputStream().apply { DataOutputStream(this).use { output ->
            val reference = pending.reference
            output.writeInt(MAGIC)
            output.writeUTF(reference.hostId); output.writeUTF(reference.hostKeyAlgorithm)
            output.writeUTF(reference.hostKeyFingerprint); output.writeUTF(reference.operationId)
            output.writeUTF(reference.platform.name); output.writeLong(reference.seenAtMillis)
            output.writeUTF(pending.mode.name); output.writeUTF(pending.kind.name); output.writeBoolean(pending.needsSudo)
            output.writeBoolean(pending.attempted)
        } }.toByteArray()
        val file = file(pending.reference.hostId)
        check(directory.isDirectory || directory.mkdirs())
        val temporary = File(directory, file.name + ".tmp")
        try {
            temporary.outputStream().use { output -> output.write(bytes); output.fd.sync() }
            Files.move(temporary.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
        } finally { temporary.delete() }
    }

    @Synchronized fun clear(pending: PendingServerInstall) {
        val current = read(pending.reference.hostId) ?: return
        require(current == pending) { "The operation or its dispatch stage changed" }
        Files.delete(file(pending.reference.hostId).toPath())
    }

    private fun validate(pending: PendingServerInstall) {
        val reference = pending.reference
        require(ServerHostTargetRules.isHostId(reference.hostId) && reference.seenAtMillis > 0)
        require(reference.hostKeyAlgorithm.length in 1..64 && reference.hostKeyAlgorithm.none(Char::isISOControl))
        require(ServerHostTrustRules.isFingerprint(reference.hostKeyFingerprint))
        require(reference.operationId.matches(Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")) &&
            UUID.fromString(reference.operationId) != UUID(0, 0))
        require(pending.kind == ServerDeploymentKind.Install || pending.kind == ServerDeploymentKind.Upgrade)
        require(if (reference.platform == ServerHostPlatform.Windows) pending.mode == ServerInstallMode.WindowsSystem
            else pending.mode == ServerInstallMode.LinuxUser || pending.mode == ServerInstallMode.LinuxSystem)
        require(!pending.needsSudo || pending.mode == ServerInstallMode.LinuxSystem)
    }

    private companion object { const val MAGIC = 0x524B5032 }
}
