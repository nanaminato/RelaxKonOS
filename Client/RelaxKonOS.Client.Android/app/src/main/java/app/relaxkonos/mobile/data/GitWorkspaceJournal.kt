package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.GitAction
import app.relaxkonos.mobile.core.net.InstallationRoutes
import java.io.*
import java.util.UUID

data class PendingGitMutation(val serviceId: String, val account: String, val repositoryId: String, val action: GitAction, val markerId: String)
class FileGitWorkspaceStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "git-workspace.bin")
    override fun read() = file.takeIf(File::isFile)?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs(); val temporary = File(directory, "git-workspace.bin.tmp"); temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Cannot persist Git mutation marker") }
    }
}
/** No branch names, file content, commit message or credentials are persisted; markers are not task keys. */
class GitWorkspaceJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(owner: SessionState.Active, id: String, action: GitAction): PendingGitMutation {
        InstallationRoutes.canonicalId(id)
        val entries = read(); require(entries.size < 100 && entries.none { it.serviceId == owner.serviceId && it.account == owner.userName && it.repositoryId == id })
        return PendingGitMutation(owner.serviceId, owner.userName, id, action, UUID.randomUUID().toString()).also { write(entries + it) }
    }
    @Synchronized fun complete(entry: PendingGitMutation) = write(read().filterNot { it == entry })
    private fun read(): List<PendingGitMutation> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B4731)
            val count = input.readInt().also { require(it in 0..100) }
            List(count) { PendingGitMutation(input.readUTF(), input.readUTF(), InstallationRoutes.canonicalId(input.readUTF()),
                GitAction.valueOf(input.readUTF()), InstallationRoutes.canonicalId(input.readUTF())) }.also {
                require(input.available() == 0 && it.map { row -> Triple(row.serviceId, row.account, row.repositoryId) }.distinct().size == it.size)
            }
        }
    }
    private fun write(entries: List<PendingGitMutation>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(0x524B4731); output.writeInt(entries.size)
            entries.forEach { output.writeUTF(it.serviceId); output.writeUTF(it.account); output.writeUTF(it.repositoryId); output.writeUTF(it.action.name); output.writeUTF(it.markerId) }
        }
        storage.write(buffer.toByteArray())
    }
}
