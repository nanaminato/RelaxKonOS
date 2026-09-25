package app.relaxkonos.mobile.security

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File

/** Byte storage for the debug credential file. */
interface DebugCredentialStorage {
    fun read(): ByteArray?
    fun write(payload: ByteArray)
    fun delete()
}

/**
 * File storage under `noBackupFilesDir`, written atomically, next to the vault files.
 *
 * `noBackupFilesDir` and not `filesDir`: the payload is a plaintext password, and it must not travel
 * through Android backup, cloud sync or device transfer even in a debug build.
 */
class FileDebugCredentialStorage(private val directory: File) : DebugCredentialStorage {
    private val file get() = File(directory, "debug-credential.bin")

    override fun read(): ByteArray? = file.takeIf { it.isFile }?.readBytes()

    override fun write(payload: ByteArray) {
        directory.mkdirs()
        val temporary = File(directory, "debug-credential.bin.tmp")
        temporary.writeBytes(payload)
        if (!temporary.renameTo(file)) {
            file.delete()
            if (!temporary.renameTo(file)) {
                temporary.delete()
                throw IllegalStateException("Unable to commit the debug credential file.")
            }
        }
    }

    override fun delete() {
        file.delete()
    }
}

/** In-memory storage used by unit tests. */
class InMemoryDebugCredentialStorage : DebugCredentialStorage {
    private var payload: ByteArray? = null

    override fun read(): ByteArray? = payload?.copyOf()

    override fun write(payload: ByteArray) {
        this.payload = payload.copyOf()
    }

    override fun delete() {
        payload = null
    }
}

/** Which identity the single debug credential belongs to. Carries no secret. */
data class DebugCredentialRecord(val serverUrl: String, val identifier: String)

/**
 * A plaintext credential store that exists only in debug builds, and only for devices where the
 * Keystore vault cannot work at all.
 *
 * `AndroidKeyStore` can only produce a key that is gated on user authentication, and
 * `setUserAuthenticationParameters` accepts nothing but strong biometrics and the device credential.
 * A device with **no lock screen at all** therefore cannot host the vault: the connection vault is
 * always reported as unavailable and `RelaxKonOS.Mobile.LoginCredentials.Design.md` §7.4 correctly
 * degrades to "type the password every time". That is the right product behaviour and it stays the
 * behaviour of every release build.
 *
 * For local development against such a device (an emulator without a PIN, a test phone), typing the
 * password into every adb-driven session is the difference between a five-second check and a chore.
 * This store is the escape hatch: **one** record, **plaintext**, in the app's private storage, read
 * back without any authorization step.
 *
 * The boundaries are structural rather than documented:
 *
 * - [app.relaxkonos.mobile.AppContainer] creates it exclusively under `BuildConfig.DEBUG`, so a
 *   release build has no instance and no code path that can write the file;
 * - [LoginViewModel][app.relaxkonos.mobile.ui.connect.LoginViewModel] offers it only while the device
 *   reports [BiometricCapability.None], i.e. exactly when the vault is impossible — never as a
 *   shortcut for a device that could use the vault, and never for the elevation vault;
 * - it holds exactly one identity: `save` replaces the previous record, so it cannot accumulate;
 * - its file format carries its own magic (`RKD1`), and a foreign or damaged file decodes as "nothing
 *   stored" instead of blocking the login screen (`AGENTS.md`: no migration for unreleased formats).
 *
 * The password is exposed only through [reveal], which hands back a copy the caller owns and zeroes,
 * exactly like an unsealed vault record. Nothing here is ever logged, and no summary of it is rendered
 * as if it were protected: the screens say "not encrypted" out loud.
 */
class DebugCredentialStore(private val storage: DebugCredentialStorage) {
    /** The identity this store holds, or `null` when it holds nothing readable. */
    fun record(): DebugCredentialRecord? {
        val parsed = parse(storage.read() ?: return null, withSecret = false) ?: return null
        return DebugCredentialRecord(parsed.serverUrl, parsed.identifier)
    }

    fun hasRecord(): Boolean = record() != null

    /** True when this store holds a password for exactly this identity. */
    fun exists(serverUrl: String, identifier: String): Boolean =
        record()?.let { it.serverUrl == serverUrl && it.identifier == identifier } ?: false

    /**
     * The password for this identity, as a fresh array the caller must zero, or `null` when the store
     * holds nothing for it. No authorization is involved: there is nothing to authorize against.
     */
    fun reveal(serverUrl: String, identifier: String): CharArray? {
        val parsed = parse(storage.read() ?: return null, withSecret = true) ?: return null
        if (parsed.serverUrl != serverUrl || parsed.identifier != identifier) {
            return null
        }
        return parsed.secret
    }

    /**
     * Stores [password] as the single record, replacing whatever was there.
     *
     * Replacing instead of appending is the whole point of "only one": the store can never grow into a
     * second password list, and there is exactly one thing for the user to delete.
     */
    fun save(serverUrl: String, identifier: String, password: CharArray) {
        val buffer = ByteArrayOutputStream()
        val secret = encodeUtf8(password)
        try {
            DataOutputStream(buffer).use { output ->
                output.writeInt(MAGIC)
                output.writeUTF(serverUrl)
                output.writeUTF(identifier)
                output.writeInt(secret.size)
                output.write(secret)
            }
        } finally {
            secret.fill(0)
        }
        storage.write(buffer.toByteArray())
    }

    /** Drops the record, but only when it belongs to this identity — same rule as the vault. */
    fun delete(serverUrl: String, identifier: String) {
        if (exists(serverUrl, identifier)) {
            storage.delete()
        }
    }

    /** Drops the record whatever it holds, for the master switch and "clear all". */
    fun clear() {
        storage.delete()
    }

    /** One parse of the file. `secret` is present only when it was asked for, and the caller owns it. */
    private class Parsed(val serverUrl: String, val identifier: String, val secret: CharArray?)

    private fun parse(payload: ByteArray, withSecret: Boolean): Parsed? = try {
        DataInputStream(ByteArrayInputStream(payload)).use { input ->
            if (input.readInt() == MAGIC) {
                val serverUrl = input.readUTF()
                val identifier = input.readUTF()
                val length = input.readInt()
                when {
                    length < 0 -> null
                    !withSecret -> Parsed(serverUrl, identifier, null)
                    else -> {
                        val bytes = ByteArray(length).also { input.readFully(it) }
                        try {
                            Parsed(serverUrl, identifier, decodeUtf8(bytes))
                        } finally {
                            bytes.fill(0)
                        }
                    }
                }
            } else {
                null
            }
        }
    } catch (_: Exception) {
        // A truncated or foreign file degrades to "nothing stored", exactly like the vault: the user
        // can always type the password instead.
        null
    }

    private companion object {
        /** `RKD1`: debug credential, version 1. A file without it is not ours. */
        const val MAGIC = 0x524B4431
    }
}
