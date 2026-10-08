package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import java.io.*

data class PendingScriptRequest(val taskId: String, val cancellation: Boolean, val attempted: Boolean = false)

class FileScriptRequestStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "script-requests.bin")
    override fun read() = file.takeIf { it.isFile }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "script-requests.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to persist script request.") }
    }
}

/** Only owner, task ID and operation type. Never command text, environment or approval credentials. */
class ScriptRequestJournal(private val storage: InstallationRequestStorage) {
    private data class Entry(val service: String, val account: String, val request: PendingScriptRequest)

    @Synchronized fun pending(owner: SessionState.Active): PendingScriptRequest? = read()
        .singleOrNull { it.service == owner.serviceId && it.account == owner.userName }?.request

    @Synchronized fun begin(owner: SessionState.Active, request: PendingScriptRequest) {
        require(validId(request.taskId))
        require(!request.attempted)
        val entries = read()
        require(entries.none { it.service == owner.serviceId && it.account == owner.userName })
        require(entries.size < 100)
        write(entries + Entry(owner.serviceId, owner.userName, request))
    }

    @Synchronized fun attempted(owner: SessionState.Active, request: PendingScriptRequest): PendingScriptRequest {
        require(!request.attempted)
        val entries = read()
        require(entries.any { it.service == owner.serviceId && it.account == owner.userName && it.request == request })
        val attempted = request.copy(attempted = true)
        write(entries.map { if (it.service == owner.serviceId && it.account == owner.userName && it.request == request)
            it.copy(request = attempted) else it })
        return attempted
    }

    @Synchronized fun complete(owner: SessionState.Active, request: PendingScriptRequest) {
        val entries = read()
        require(entries.any { it.service == owner.serviceId && it.account == owner.userName && it.request == request })
        write(entries.filterNot { it.service == owner.serviceId && it.account == owner.userName && it.request == request })
    }

    private fun read(): List<Entry> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B5332)
            val count = input.readInt().also { require(it in 0..100) }
            val entries = List(count) {
                val service = input.readUTF(); val account = input.readUTF(); val id = input.readUTF()
                require(service.isNotBlank() && account.isNotBlank() && validId(id))
                Entry(service, account, PendingScriptRequest(id, input.readBoolean(), input.readBoolean()))
            }
            require(input.available() == 0)
            require(entries.map { it.service to it.account }.distinct().size == entries.size)
            entries
        }
    }

    private fun validId(id: String) = id.isNotBlank() && id.length <= 128 && id.none { it.code < 32 || it.code == 127 }

    private fun write(entries: List<Entry>) {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { output ->
            output.writeInt(0x524B5332); output.writeInt(entries.size)
            entries.forEach { output.writeUTF(it.service); output.writeUTF(it.account)
                output.writeUTF(it.request.taskId); output.writeBoolean(it.request.cancellation); output.writeBoolean(it.request.attempted) }
        }
        storage.write(bytes.toByteArray())
    }
}
