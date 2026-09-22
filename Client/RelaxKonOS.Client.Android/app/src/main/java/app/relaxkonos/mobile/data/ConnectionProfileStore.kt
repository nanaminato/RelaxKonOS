package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.security.model.SavedConnection
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
 * The connection list shown on the login and connections screens.
 *
 * Only the address and the login identifier are stored here; whether a password is available is
 * answered by the connection vault, so there is exactly one source of truth for that question.
 */
class ConnectionProfileStore(private val storage: ProfileStorage) {
    fun all(): List<SavedConnection> = read().sortedByDescending { it.lastUsedEpochMillis }

    fun recent(): SavedConnection? = all().firstOrNull()

    fun upsert(connection: SavedConnection) {
        val updated = read().filterNot { it.serverUrl == connection.serverUrl && it.identifier == connection.identifier } + connection
        write(updated)
    }

    fun remove(serverUrl: String) {
        write(read().filterNot { it.serverUrl == serverUrl })
    }

    private fun read(): List<SavedConnection> {
        val payload = storage.read() ?: return emptyList()
        return try {
            DataInputStream(ByteArrayInputStream(payload)).use { input ->
                if (input.readInt() != MAGIC) {
                    emptyList()
                } else {
                    val count = input.readInt()
                    ArrayList<SavedConnection>(count).apply {
                        repeat(count) {
                            add(SavedConnection(input.readUTF(), input.readUTF(), input.readLong()))
                        }
                    }
                }
            }
        } catch (_: Exception) {
            // A damaged profile file must not block the login screen.
            emptyList()
        }
    }

    private fun write(connections: List<SavedConnection>) {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(MAGIC)
            output.writeInt(connections.size)
            for (connection in connections) {
                output.writeUTF(connection.serverUrl)
                output.writeUTF(connection.identifier)
                output.writeLong(connection.lastUsedEpochMillis)
            }
        }
        storage.write(buffer.toByteArray())
    }

    private companion object {
        const val MAGIC = 0x524B4331
    }
}
