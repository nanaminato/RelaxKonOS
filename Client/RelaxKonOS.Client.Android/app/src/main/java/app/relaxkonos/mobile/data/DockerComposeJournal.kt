package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import java.io.*
import java.util.UUID

data class PendingDockerCompose(val serviceId: String, val account: String, val project: String, val action: String, val requestKey: String)

class FileDockerComposeStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "docker-compose-requests.bin")
    override fun read() = file.takeIf(File::isFile)?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "docker-compose-requests.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to persist Compose request") }
    }
}

/** Request identity only: no YAML, proxy credentials, tokens or automatic replay payload. */
class DockerComposeJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(owner: SessionState.Active, project: String, action: String): PendingDockerCompose {
        validate(project, action)
        val records = read()
        require(records.size < 100 && records.none { it.serviceId == owner.serviceId && it.account == owner.userName })
        return PendingDockerCompose(owner.serviceId, owner.userName, project, action, UUID.randomUUID().toString()).also { write(records + it) }
    }
    @Synchronized fun complete(record: PendingDockerCompose) = write(read().filterNot { it == record })
    private fun validate(project: String, action: String) {
        require(project.length in 1..128 && project.none { it.code < 32 || it.code == 127 })
        require(action in setOf("deploy", "start", "stop", "restart", "delete"))
    }
    private fun read(): List<PendingDockerCompose> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B4331)
            val count = input.readInt().also { require(it in 0..100) }
            List(count) {
                PendingDockerCompose(input.readUTF(), input.readUTF(), input.readUTF(), input.readUTF(), input.readUTF()).also {
                    validate(it.project, it.action); require(UUID.fromString(it.requestKey).toString() == it.requestKey)
                }
            }.also { require(input.available() == 0) }
        }
    }
    private fun write(records: List<PendingDockerCompose>) {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { output ->
            output.writeInt(0x524B4331); output.writeInt(records.size)
            records.forEach { output.writeUTF(it.serviceId); output.writeUTF(it.account); output.writeUTF(it.project); output.writeUTF(it.action); output.writeUTF(it.requestKey) }
        }
        storage.write(bytes.toByteArray())
    }
}
