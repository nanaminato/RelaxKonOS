package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.security.model.SavedLogin
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File

/** Byte storage for the connection profile list. */
interface ProfileStorage {
    fun read(): ByteArray?
    fun write(payload: ByteArray)
}

/** File storage under `noBackupFilesDir`, written atomically. */
class FileProfileStorage(private val directory: File) : ProfileStorage {
    private val file get() = File(directory, "connections.bin")

    override fun read(): ByteArray? = file.takeIf { it.isFile }?.readBytes()

    override fun write(payload: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "connections.bin.tmp")
        temporary.writeBytes(payload)
        if (!temporary.renameTo(file)) {
            file.delete()
            if (!temporary.renameTo(file)) {
                temporary.delete()
                throw IllegalStateException("Unable to commit the connection profile file.")
            }
        }
    }
}

/** In-memory storage used by unit tests. */
class InMemoryProfileStorage : ProfileStorage {
    private var payload: ByteArray? = null

    override fun read(): ByteArray? = payload?.copyOf()

    override fun write(payload: ByteArray) {
        this.payload = payload.copyOf()
    }
}

/**
 * The login list shown on the sign-in and connections screens.
 *
 * Every mutation addresses one login through the pair `(serviceId, identifier)`. There is deliberately
 * no operation that acts on a service identity alone: one server can hold several accounts, and removing
 * "the server" would silently take the other accounts' records with it
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §6.3).
 *
 * The list holds no secret — the password is in the connection vault — and the one flag it does carry
 * about credentials ([SavedLogin.hasSavedCredential]) is a projection that is reconciled against the
 * vault rather than trusted as the truth (§2.2, §4.2).
 */
class ConnectionProfileStore(private val storage: ProfileStorage) {
    fun all(): List<SavedLogin> = read().sortedByDescending { it.lastUsedEpochMillis }

    fun recent(): SavedLogin? = all().firstOrNull()

    fun upsert(login: SavedLogin) {
        write(read().filterNot { it.sameIdentityAs(login) } + login)
    }

    /** Removes exactly one login, leaving every other account on the same server untouched. */
    fun remove(serviceId: String, identifier: String) {
        write(read().filterNot { it.serviceId == serviceId && it.identifier == identifier })
    }

    /** Updates the credential projection for one login, or does nothing when it is not stored. */
    fun setHasSavedCredential(serviceId: String, identifier: String, hasCredential: Boolean) {
        val current = read()
        if (current.none { it.serviceId == serviceId && it.identifier == identifier }) {
            return
        }
        write(
            current.map {
                if (it.serviceId == serviceId && it.identifier == identifier) {
                    it.copy(hasSavedCredential = hasCredential)
                } else {
                    it
                }
            },
        )
    }

    /**
     * Re-aligns one projection with the vault, which is the only source of truth for "is a credential
     * stored". Called once at start-up so a projection written by an older build, or left behind by an
     * interrupted operation, cannot outlive the record it describes (§2.2). The file is only rewritten
     * when something actually differs.
     */
    fun reconcileCredentialProjection(exists: (serviceId: String, identifier: String) -> Boolean) {
        val current = read()
        val reconciled = current.map { it.copy(hasSavedCredential = exists(it.serviceId, it.identifier)) }
        if (reconciled != current) {
            write(reconciled)
        }
    }

    private fun SavedLogin.sameIdentityAs(other: SavedLogin): Boolean =
        serviceId == other.serviceId && identifier == other.identifier

    private fun read(): List<SavedLogin> {
        val payload = storage.read() ?: return emptyList()
        return try {
            DataInputStream(ByteArrayInputStream(payload)).use { input ->
                if (input.readInt() != MAGIC) {
                    emptyList()
                } else {
                    val count = input.readInt()
                    ArrayList<SavedLogin>(count).apply {
                        repeat(count) {
                            val serviceId = input.readUTF()
                            val identifier = input.readUTF()
                            val lastUsed = input.readLong()
                            val displayName = if (input.readByte().toInt() == 1) input.readUTF() else null
                            val hasCredential = input.readByte().toInt() == 1
                            add(SavedLogin(serviceId, identifier, lastUsed, displayName, hasCredential))
                        }
                    }
                }
            }
        } catch (_: Exception) {
            // A damaged profile file must not block the login screen.
            emptyList()
        }
    }

    private fun write(logins: List<SavedLogin>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(MAGIC)
            output.writeInt(logins.size)
            for (login in logins) {
                output.writeUTF(login.serviceId)
                output.writeUTF(login.identifier)
                output.writeLong(login.lastUsedEpochMillis)
                val displayName = login.displayName
                output.writeByte(if (displayName == null) 0 else 1)
                if (displayName != null) {
                    output.writeUTF(displayName)
                }
                output.writeByte(if (login.hasSavedCredential) 1 else 0)
            }
        }
        storage.write(buffer.toByteArray())
    }

    private companion object {
        /** `RKC2`: the first string is serviceId; direct records already store that canonical URL. */
        const val MAGIC = 0x524B4332
    }
}
