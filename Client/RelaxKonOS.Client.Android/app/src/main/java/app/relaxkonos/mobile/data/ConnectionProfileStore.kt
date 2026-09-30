package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.HostOperatingSystemKind
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
 * Credential and deletion operations address one login through the pair `(serviceId, identifier)`:
 * one server can hold several accounts, and removing
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

    /**
     * Stores the host's answer for every account on this service, without changing credentials.
     *
     * Returns whether the file was written: a login that is gone is never resurrected by a background
     * answer, and an answer that changes nothing does not rewrite the file — the list is opened often,
     * and a write per open would be churn with no visible difference. Nothing else writes this field,
     * so a value here always came from the server; a failed request never reaches this method.
     */
    fun setHostOperatingSystem(serviceId: String, kind: HostOperatingSystemKind): Boolean {
        val current = read()
        if (current.none { it.serviceId == serviceId && it.hostOperatingSystem != kind }) return false
        write(
            current.map {
                if (it.serviceId == serviceId) {
                    it.copy(hostOperatingSystem = kind)
                } else {
                    it
                }
            },
        )
        return true
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
                            val hostOs = if (input.readByte().toInt() == 1) input.readUTF() else null
                            add(
                                SavedLogin(
                                    serviceId, identifier, lastUsed, displayName, hasCredential,
                                    hostOs?.let(HostOperatingSystemKind::fromWire),
                                ),
                            )
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
                val hostOs = login.hostOperatingSystem
                output.writeByte(if (hostOs == null) 0 else 1)
                if (hostOs != null) {
                    output.writeUTF(hostOs.name)
                }
            }
        }
        storage.write(buffer.toByteArray())
    }

    private companion object {
        /**
         * `RKC3`: `RKC2` plus the host operating system class of each login.
         *
         * The layout gained a field, so the older magic is discarded rather than migrated — the same
         * rule the vault and the account archive follow (`RelaxKonOS.Mobile.LoginCredentials.Design.md`
         * §2.2). What is lost is the list of remembered endpoints and accounts; every password stays in
         * the vault, and signing in again brings the row back with it.
         */
        const val MAGIC = 0x524B4333
    }
}
