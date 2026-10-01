package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import java.io.*

/** Lookup metadata only. Environment values, plan differences and credentials are never persisted. */
data class HostSettingsReference(val serviceId: String, val account: String, val kind: HostSettingKind, val id: String,
    val target: HostSettingsTarget, val expiresAt: String, val unresolved: Boolean)
class FileHostSettingsStorage(private val directory: File) : InstallationRequestStorage {
    private val file get() = File(directory, "host-settings.bin")
    override fun read() = file.takeIf { it.isFile }?.readBytes()
    override fun write(bytes: ByteArray) {
        directory.mkdirs()
        val temp = File(directory, "host-settings.bin.tmp"); temp.writeBytes(bytes)
        if (!temp.renameTo(file)) { temp.delete(); error("Unable to persist settings references") }
    }
}
class HostSettingsJournal(private val storage: InstallationRequestStorage) {
    @Synchronized fun references(owner: SessionState.Active) = read().filter { it.serviceId == owner.serviceId && it.account == owner.userName }.reversed()
    @Synchronized fun record(owner: SessionState.Active, kind: HostSettingKind, plan: HostSettingsPlan): HostSettingsReference {
        val rows = read()
        require(rows.none { it.serviceId == owner.serviceId && it.account == owner.userName && it.unresolved })
        val entry = HostSettingsReference(owner.serviceId, owner.userName, kind, plan.id, plan.target, plan.expiresAt, true)
        val kept = rows.filter { it.unresolved } + rows.filterNot { it.unresolved }.takeLast(99 - rows.count { it.unresolved })
        require(kept.size < 100)
        write(kept + entry); return entry
    }
    @Synchronized fun resolved(entry: HostSettingsReference) = write(read().map { if (same(it, entry)) it.copy(unresolved = false) else it })
    @Synchronized fun beginRollback(entry: HostSettingsReference): HostSettingsReference {
        val rows = read(); require(rows.any { same(it, entry) }); require(rows.none { it.serviceId == entry.serviceId && it.account == entry.account && it.unresolved })
        val updated = entry.copy(unresolved = true); write(rows.map { if (same(it, entry)) updated else it }); return updated
    }
    private fun same(a: HostSettingsReference, b: HostSettingsReference) = a.serviceId == b.serviceId && a.account == b.account && a.id == b.id
    private fun read(): List<HostSettingsReference> {
        val bytes = storage.read() ?: return emptyList()
        require(bytes.size <= 1024 * 1024)
        return DataInputStream(ByteArrayInputStream(bytes)).use { input ->
            require(input.readInt() == 0x524B5331)
            List(input.readInt().also { require(it in 0..100) }) {
                HostSettingsReference(input.readUTF(), input.readUTF(), HostSettingKind.valueOf(input.readUTF()), HostSettingsRules.id(input.readUTF()),
                    HostSettingsTarget(input.readUTF(), input.readUTF(), input.readUTF().ifEmpty { null }), input.readUTF().also { IsoInstant.requireEpochMillis(it) }, input.readBoolean())
            }.also { require(input.available() == 0) }
        }
    }
    private fun write(rows: List<HostSettingsReference>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { out ->
            out.writeInt(0x524B5331); out.writeInt(rows.size)
            rows.forEach { out.writeUTF(it.serviceId); out.writeUTF(it.account); out.writeUTF(it.kind.name); out.writeUTF(it.id)
                out.writeUTF(it.target.resourceId); out.writeUTF(it.target.scope); out.writeUTF(it.target.platformIdentity.orEmpty()); out.writeUTF(it.expiresAt); out.writeBoolean(it.unresolved) }
        }; storage.write(buffer.toByteArray())
    }
}
