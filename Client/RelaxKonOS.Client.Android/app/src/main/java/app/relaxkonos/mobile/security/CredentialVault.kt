package app.relaxkonos.mobile.security

import android.security.keystore.KeyPermanentlyInvalidatedException
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.security.GeneralSecurityException
import java.security.UnrecoverableKeyException
import javax.crypto.Cipher

/** The two credential domains. They are never merged, shared or cross-filled. */
enum class VaultKind { Connection, Elevation }

/**
 * One stored credential. The key material never lives here: only the GCM payload produced by a
 * Keystore key that requires user authentication.
 */
class VaultRecord(
    val kind: VaultKind,
    val serverUrl: String,
    val account: String,
    val lastUsedEpochMillis: Long,
    val fingerprintProtected: Boolean,
    val iv: ByteArray,
    val ciphertext: ByteArray,
) {
    val id: String get() = recordId(kind, serverUrl, account)

    override fun equals(other: Any?): Boolean = other is VaultRecord && other.id == id

    override fun hashCode(): Int = id.hashCode()
}

/** Stable record identity: `kind|serverUrl|account`. */
fun recordId(kind: VaultKind, serverUrl: String, account: String): String = "${kind.name}|$serverUrl|$account"

/**
 * Additional authenticated data binding a payload to its record identity. Moving a ciphertext under
 * a different server or account makes decryption fail instead of silently succeeding.
 */
fun vaultAad(kind: VaultKind, serverUrl: String, account: String): ByteArray =
    "relaxkonos-vault|${kind.name}|$serverUrl|$account".toByteArray(Charsets.UTF_8)

/** Raised when the Keystore key can no longer be used, e.g. after a new biometric enrolment. */
class VaultKeyInvalidatedException(message: String, cause: Throwable? = null) : Exception(message, cause)

/** Raised when a stored payload fails authentication: the record must be treated as unusable. */
class VaultTamperException(message: String, cause: Throwable? = null) : Exception(message, cause)

/** Keystore operations the vault depends on. Implemented by [VaultKeyManager]; faked in tests. */
interface VaultCrypto {
    /** A Cipher ready for encryption, bound to whatever authentication its key requires. */
    fun sealCipher(kind: VaultKind): Cipher

    /** A Cipher ready for decryption of a record produced with [iv]. */
    fun openCipher(kind: VaultKind, iv: ByteArray): Cipher
}

/** Byte storage for one vault file per kind. */
interface VaultStorage {
    fun read(kind: VaultKind): ByteArray?
    fun write(kind: VaultKind, payload: ByteArray)
    fun delete(kind: VaultKind)
}

/**
 * File storage under the app's `noBackupFilesDir`, so ciphertext never travels through Android
 * backup, cloud sync or device transfer. Writes are atomic through a temporary file rename.
 */
class FileVaultStorage(private val directory: File) : VaultStorage {
    private fun file(kind: VaultKind) = File(directory, "${kind.name.lowercase()}-vault.bin")

    override fun read(kind: VaultKind): ByteArray? = file(kind).takeIf { it.isFile }?.readBytes()

    override fun write(kind: VaultKind, payload: ByteArray) {
        directory.mkdirs()
        val target = file(kind)
        val temporary = File(directory, target.name + ".tmp")
        temporary.writeBytes(payload)
        if (!temporary.renameTo(target)) {
            target.delete()
            if (!temporary.renameTo(target)) {
                temporary.delete()
                throw IllegalStateException("Unable to commit credential vault file.")
            }
        }
    }

    override fun delete(kind: VaultKind) {
        file(kind).delete()
    }
}

/** Test storage holding the payload in memory. */
class InMemoryVaultStorage : VaultStorage {
    private val payloads = mutableMapOf<VaultKind, ByteArray>()

    override fun read(kind: VaultKind): ByteArray? = payloads[kind]?.copyOf()

    override fun write(kind: VaultKind, payload: ByteArray) {
        payloads[kind] = payload.copyOf()
    }

    override fun delete(kind: VaultKind) {
        payloads.remove(kind)
    }
}

/**
 * The client-side credential vault. Holds the two independent vaults required by
 * `RelaxKonOS.Mobile.V1.Design.md` §5.2 and enforces the invariants of §5.3:
 *
 * - payloads are bound to `vault|serverUrl|account` through AES-GCM additional authenticated data;
 * - a record can only be opened through its own [VaultRecord], so a connection payload cannot be
 *   read back as an elevation credential;
 * - plaintext passwords are handled as `CharArray` and zeroed by the caller.
 *
 * The vault never touches Keystore directly: [VaultCrypto] supplies the Cipher, which keeps the
 * whole storage and binding logic verifiable in JVM unit tests.
 */
class CredentialVault(
    private val storage: VaultStorage,
    private val crypto: VaultCrypto,
) {
    fun records(kind: VaultKind): List<VaultRecord> = read(kind)

    fun record(kind: VaultKind, serverUrl: String, account: String): VaultRecord? =
        read(kind).firstOrNull { it.serverUrl == serverUrl && it.account == account }

    fun hasRecords(kind: VaultKind): Boolean = read(kind).isNotEmpty()

    /** Starts a save. The returned Cipher must be authorized before it can encrypt anything. */
    fun beginSeal(kind: VaultKind): Cipher = crypto.sealCipher(kind)

    /** Stores [password] with an authorized [cipher]. */
    fun seal(
        kind: VaultKind,
        serverUrl: String,
        account: String,
        password: CharArray,
        cipher: Cipher,
        fingerprintProtected: Boolean,
        nowEpochMillis: Long,
    ): VaultRecord {
        val plaintext = encodeUtf8(password)
        val ciphertext = try {
            cipher.updateAAD(vaultAad(kind, serverUrl, account))
            cipher.doFinal(plaintext)
        } catch (error: GeneralSecurityException) {
            throw VaultTamperException("Unable to seal the ${kind.name} credential.", error)
        } finally {
            plaintext.fill(0)
        }
        val record = VaultRecord(
            kind = kind,
            serverUrl = serverUrl,
            account = account,
            lastUsedEpochMillis = nowEpochMillis,
            fingerprintProtected = fingerprintProtected,
            iv = cipher.iv,
            ciphertext = ciphertext,
        )
        write(kind, read(kind).filterNot { it.id == record.id } + record)
        return record
    }

    /** Starts an unlock. The returned Cipher must be authorized before it can decrypt anything. */
    fun beginOpen(record: VaultRecord): Cipher = crypto.openCipher(record.kind, record.iv)

    /** Decrypts a record with an authorized [cipher]. */
    fun open(record: VaultRecord, cipher: Cipher): CharArray {
        val plaintext = try {
            cipher.updateAAD(vaultAad(record.kind, record.serverUrl, record.account))
            cipher.doFinal(record.ciphertext)
        } catch (error: GeneralSecurityException) {
            throw VaultTamperException("Unable to open the ${record.kind.name} credential.", error)
        }
        return try {
            decodeUtf8(plaintext)
        } finally {
            plaintext.fill(0)
        }
    }

    /** Touches the "last used" stamp after a successful server-side verification. */
    fun markUsed(record: VaultRecord, nowEpochMillis: Long) {
        val current = read(record.kind)
        if (current.none { it.id == record.id }) {
            return
        }
        write(
            record.kind,
            current.map {
                if (it.id == record.id) {
                    VaultRecord(it.kind, it.serverUrl, it.account, nowEpochMillis, it.fingerprintProtected, it.iv, it.ciphertext)
                } else {
                    it
                }
            },
        )
    }

    /**
     * Drops a single record. Called by the account and security page and whenever the server
     * rejects a stored credential (`RelaxKonOS.Mobile.V1.Design.md` §5.8.2).
     */
    fun delete(kind: VaultKind, serverUrl: String, account: String) {
        val id = recordId(kind, serverUrl, account)
        write(kind, read(kind).filterNot { it.id == id })
    }

    fun delete(record: VaultRecord) = delete(record.kind, record.serverUrl, record.account)

    /** Clears one vault completely, e.g. when the fingerprint master switch is turned off. */
    fun clear(kind: VaultKind) {
        storage.delete(kind)
    }

    /** Clears the given records only, preserving the server and account entries themselves. */
    fun clearRecords(kind: VaultKind, records: List<VaultRecord>) {
        val ids = records.map { it.id }.toSet()
        write(kind, read(kind).filterNot { it.id in ids })
    }

    private fun read(kind: VaultKind): List<VaultRecord> {
        val payload = storage.read(kind) ?: return emptyList()
        return VaultFileFormat.decode(kind, payload)
    }

    private fun write(kind: VaultKind, records: List<VaultRecord>) {
        if (records.isEmpty()) {
            storage.delete(kind)
            return
        }
        storage.write(kind, VaultFileFormat.encode(records))
    }
}

/**
 * Binary container for vault records. A private, fixed layout is used instead of JSON so the same
 * code path runs on the device and in JVM unit tests.
 *
 * `magic("RKV1") | kindOrdinal(u8) | count(i32) | record*`
 * `record = url(UTF) | account(UTF) | lastUsed(i64) | protected(u8) | iv(byte[]) | ciphertext(byte[])`
 */
internal object VaultFileFormat {
    private const val MAGIC = 0x524B5631

    fun encode(records: List<VaultRecord>): ByteArray {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(MAGIC)
            output.writeByte(records.first().kind.ordinal)
            output.writeInt(records.size)
            for (record in records) {
                output.writeUTF(record.serverUrl)
                output.writeUTF(record.account)
                output.writeLong(record.lastUsedEpochMillis)
                output.writeByte(if (record.fingerprintProtected) 1 else 0)
                output.writeInt(record.iv.size)
                output.write(record.iv)
                output.writeInt(record.ciphertext.size)
                output.write(record.ciphertext)
            }
        }
        return buffer.toByteArray()
    }

    fun decode(kind: VaultKind, payload: ByteArray): List<VaultRecord> = try {
        DataInputStream(ByteArrayInputStream(payload)).use { input ->
            if (input.readInt() != MAGIC || input.readByte().toInt() != kind.ordinal) {
                emptyList()
            } else {
                val count = input.readInt()
                val records = ArrayList<VaultRecord>(count)
                repeat(count) {
                    val serverUrl = input.readUTF()
                    val account = input.readUTF()
                    val lastUsed = input.readLong()
                    val protected = input.readByte().toInt() == 1
                    val iv = ByteArray(input.readInt()).also { input.readFully(it) }
                    val ciphertext = ByteArray(input.readInt()).also { input.readFully(it) }
                    records += VaultRecord(kind, serverUrl, account, lastUsed, protected, iv, ciphertext)
                }
                records
            }
        }
    } catch (_: Exception) {
        // A truncated or foreign file degrades to "no stored credentials" rather than crashing the
        // shell: the user can always type the password again.
        emptyList()
    }
}

internal fun encodeUtf8(value: CharArray): ByteArray {
    val buffer = Charsets.UTF_8.newEncoder().encode(java.nio.CharBuffer.wrap(value))
    val bytes = ByteArray(buffer.remaining())
    buffer.get(bytes)
    return bytes
}

internal fun decodeUtf8(bytes: ByteArray): CharArray {
    val buffer = Charsets.UTF_8.newDecoder().decode(java.nio.ByteBuffer.wrap(bytes))
    val chars = CharArray(buffer.remaining())
    buffer.get(chars)
    return chars
}

/** Maps Keystore failures onto the vault's own invalidation signal. */
internal fun mapKeyException(kind: VaultKind, error: Throwable): Nothing = when (error) {
    is VaultKeyInvalidatedException -> throw error
    is KeyPermanentlyInvalidatedException ->
        throw VaultKeyInvalidatedException("The ${kind.name} vault key was invalidated by a biometric change.", error)

    is UnrecoverableKeyException ->
        throw VaultKeyInvalidatedException("The ${kind.name} vault key can no longer be recovered.", error)

    is GeneralSecurityException ->
        throw VaultKeyInvalidatedException("Keystore rejected the ${kind.name} vault key.", error)

    else -> throw error
}
