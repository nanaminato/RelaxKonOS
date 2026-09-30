package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import java.io.*

/** Synchronous FRP writes have no operation ID/key. Keep a secret-free lookup marker until explicitly observed. */
enum class TunnelMutation { SaveProfile, DeleteProfile, SetToken, SaveDefinition, DeleteDefinition, Apply, Stop, SaveFrps, StartFrps, StopFrps, RestartFrps;
    val frps get() = this in setOf(SaveFrps, StartFrps, StopFrps, RestartFrps)
}
data class PendingTunnelMutation(val serviceId: String, val account: String, val action: TunnelMutation, val target: String?, val profileId: String?)
class FileTunnelMutationStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "tunnel-mutations.bin")
    override fun read() = file.takeIf { it.isFile }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs(); val temporary = File(directory, "tunnel-mutations.bin.tmp")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.delete(); error("Unable to persist tunnel observation marker.") }
    }
}
class TunnelMutationJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun pending(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }
    @Synchronized fun begin(entry: PendingTunnelMutation): Boolean {
        val entries = read()
        if (entries.any { it.serviceId == entry.serviceId && it.account == entry.account }) return false
        require(entries.size < 100); write(entries + entry); return true
    }
    @Synchronized fun complete(entry: PendingTunnelMutation) = write(read().filterNot { it == entry })
    private fun read(): List<PendingTunnelMutation> {
        val bytes = storage.read() ?: return emptyList()
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B5431)
            val entries = List(input.readInt().also { require(it in 0..100) }) {
                PendingTunnelMutation(input.readUTF(), input.readUTF(), TunnelMutation.valueOf(input.readUTF()),
                    input.readUTF().takeIf(String::isNotEmpty), input.readUTF().takeIf(String::isNotEmpty))
            }
            require(input.available() == 0); entries
        }
    }
    private fun write(entries: List<PendingTunnelMutation>) {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { output ->
            output.writeInt(0x524B5431); output.writeInt(entries.size)
            entries.forEach { output.writeUTF(it.serviceId); output.writeUTF(it.account); output.writeUTF(it.action.name); output.writeUTF(it.target.orEmpty()); output.writeUTF(it.profileId.orEmpty()) }
        }; storage.write(bytes.toByteArray())
    }
}
