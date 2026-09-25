package app.relaxkonos.mobile.security

import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.UserNotAuthenticatedException
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.security.GeneralSecurityException
import java.security.UnrecoverableKeyException
import javax.crypto.Cipher

/**
 * The credential domains. They are never merged, shared or cross-filled.
 *
 * [Ssh] is the server centre's SSH credential domain. It is a separate kind rather than a reuse of
 * [Connection] because an SSH credential is bound to a host endpoint and SSH user, not to a RelaxKonOS
 * service identity and login identifier; two records that happen to hold the same password are still two
 * records (`RelaxKonOS.Mobile.ServerCenter.Design.md` §3).
 */
enum class VaultKind { Connection, Elevation, Ssh }

/**
 * Whether a stored payload may still be decrypted.
 *
 * A record whose key was permanently invalidated — typically because the user enrolled a new
 * fingerprint — cannot be read again, but it is **not** deleted: the fact that this identity once had a
 * saved password is information the user needs, and the only thing that removes a record is an explicit
 * delete (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §7.4, D5).
 */
enum class VaultRecordState { Sealed, Invalidated }

/**
 * One stored credential. The key material never lives here: only the GCM payload produced by a
 * Keystore key that requires user authentication.
 *
 * [iv] and [ciphertext] are retained even when [state] is [VaultRecordState.Invalidated] — the vault
 * refuses to read them, and keeping them means a later save overwrites one record rather than leaving
 * an unreadable orphan behind.
 */
class VaultRecord(
    val kind: VaultKind,
    val serviceId: String,
    val account: String,
    val lastUsedEpochMillis: Long,
    val fingerprintProtected: Boolean,
    val iv: ByteArray,
    val ciphertext: ByteArray,
    val state: VaultRecordState = VaultRecordState.Sealed,
) {
    val id: String get() = recordId(kind, serviceId, account)

    override fun equals(other: Any?): Boolean = other is VaultRecord && other.id == id

    override fun hashCode(): Int = id.hashCode()
}

/** Stable record identity: `kind|serviceId|account`. */
fun recordId(kind: VaultKind, serviceId: String, account: String): String = "${kind.name}|$serviceId|$account"

/**
 * Additional authenticated data binding a payload to its record identity. Moving a ciphertext under
 * a different service identity or account makes decryption fail instead of silently succeeding.
 */
fun vaultAad(kind: VaultKind, serviceId: String, account: String): ByteArray =
    "relaxkonos-vault|${kind.name}|$serviceId|$account".toByteArray(Charsets.UTF_8)

/** Raised when the Keystore key can no longer be used, e.g. after a new biometric enrolment. */
class VaultKeyInvalidatedException(message: String, cause: Throwable? = null) : Exception(message, cause)

/** Raised when a stored payload fails authentication: the record must be treated as unusable. */
class VaultTamperException(message: String, cause: Throwable? = null) : Exception(message, cause)

/**
 * Raised when a record is read after it was marked invalidated.
 *
 * Separate from [VaultKeyInvalidatedException] because it is a decision this client already made, not a
 * Keystore verdict, but callers map both onto the same user-facing outcome: the saved password cannot
 * be unsealed and must be typed instead.
 */
class VaultRecordInvalidatedException(message: String) : Exception(message)

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
 * The client-side credential vault. Holds the independent vaults required by
 * `RelaxKonOS.Mobile.V1.Design.md` §5.2 and enforces the invariants of §5.3:
 *
 * - payloads are bound to `vault|serviceId|account` through AES-GCM additional authenticated data;
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

    fun record(kind: VaultKind, serviceId: String, account: String): VaultRecord? =
        read(kind).firstOrNull { it.serviceId == serviceId && it.account == account }

    fun hasRecords(kind: VaultKind): Boolean = read(kind).isNotEmpty()

    /** Starts a save. The returned Cipher must be authorized before it can encrypt anything. */
    fun beginSeal(kind: VaultKind): Cipher = crypto.sealCipher(kind)

    /**
     * Stores [password] with an authorized [cipher].
     *
     * A record that already exists at the same identity is replaced, whatever its state: saving again
     * is how an invalidated record is refreshed, so this is also the only way a record returns from
     * [VaultRecordState.Invalidated] to [VaultRecordState.Sealed].
     */
    fun seal(
        kind: VaultKind,
        serviceId: String,
        account: String,
        password: CharArray,
        cipher: Cipher,
        fingerprintProtected: Boolean,
        nowEpochMillis: Long,
    ): VaultRecord {
        val plaintext = encodeUtf8(password)
        val ciphertext = try {
            cipher.updateAAD(vaultAad(kind, serviceId, account))
            cipher.doFinal(plaintext)
        } catch (error: GeneralSecurityException) {
            throw VaultTamperException("Unable to seal the ${kind.name} credential.", error)
        } finally {
            plaintext.fill(0)
        }
        val record = VaultRecord(
            kind = kind,
            serviceId = serviceId,
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
    fun beginOpen(record: VaultRecord): Cipher {
        requireSealed(record)
        return crypto.openCipher(record.kind, record.iv)
    }

    /**
     * Decrypts a record with an authorized [cipher].
     *
     * An invalidated record is refused here as well as in [beginOpen]: the second gate matters because
     * a caller may hold a record that was marked invalid between the two calls.
     */
    fun open(record: VaultRecord, cipher: Cipher): CharArray {
        requireSealed(record)
        val plaintext = try {
            cipher.updateAAD(vaultAad(record.kind, record.serviceId, record.account))
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

    /**
     * Marks one record as permanently unreadable, keeping the record and its ciphertext.
     *
     * Called when the Keystore reports a permanently invalidated key. The payload is deliberately not
     * destroyed and the record is deliberately not removed: the user must be able to see that this
     * identity once had a saved password and why it stopped working (D5).
     */
    fun markInvalidated(record: VaultRecord) {
        val current = read(record.kind)
        if (current.none { it.id == record.id && it.state != VaultRecordState.Invalidated }) {
            return
        }
        write(
            record.kind,
            current.map { if (it.id == record.id) it.asInvalidated() else it },
        )
    }

    /**
     * Marks every record in one vault as permanently unreadable, retaining their encrypted payloads.
     *
     * A [VaultKind] has one Keystore alias, not one key per record. Once that key is permanently
     * invalidated, none of the records sealed by it can be opened. Marking the entire vault prevents
     * the misleading state where only the first attempted login says "invalidated" while each sibling
     * credential is equally unrecoverable. A later explicit save may create a replacement key and seal
     * only the identity the user just authenticated as.
     */
    fun markAllInvalidated(kind: VaultKind) {
        val current = read(kind)
        if (current.none { it.state != VaultRecordState.Invalidated }) {
            return
        }
        write(kind, current.map { it.asInvalidated() })
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
                    VaultRecord(
                        it.kind,
                        it.serviceId,
                        it.account,
                        nowEpochMillis,
                        it.fingerprintProtected,
                        it.iv,
                        it.ciphertext,
                        it.state,
                    )
                } else {
                    it
                }
            },
        )
    }

    /** Drops a single record: the explicit "forget the password" action of the design (§6.3). */
    fun delete(kind: VaultKind, serviceId: String, account: String) {
        val id = recordId(kind, serviceId, account)
        write(kind, read(kind).filterNot { it.id == id })
    }

    fun delete(record: VaultRecord) = delete(record.kind, record.serviceId, record.account)

    /** Clears one vault completely, e.g. when the fingerprint master switch is turned off. */
    fun clear(kind: VaultKind) {
        storage.delete(kind)
    }

    /** Clears the given records only, preserving the server and account entries themselves. */
    fun clearRecords(kind: VaultKind, records: List<VaultRecord>) {
        val ids = records.map { it.id }.toSet()
        write(kind, read(kind).filterNot { it.id in ids })
    }

    private fun requireSealed(record: VaultRecord) {
        if (record.state == VaultRecordState.Invalidated) {
            throw VaultRecordInvalidatedException(
                "The ${record.kind.name} credential for ${record.account} was invalidated and is not readable.",
            )
        }
    }

    private fun VaultRecord.asInvalidated(): VaultRecord = VaultRecord(
        kind = kind,
        serviceId = serviceId,
        account = account,
        lastUsedEpochMillis = lastUsedEpochMillis,
        fingerprintProtected = fingerprintProtected,
        iv = iv,
        ciphertext = ciphertext,
        state = VaultRecordState.Invalidated,
    )

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
 * `magic("RKV2") | kindOrdinal(u8) | count(i32) | record*`
 * `record = serviceId(UTF) | account(UTF) | lastUsed(i64) | protected(u8) | state(u8) | iv(byte[]) | ciphertext(byte[])`
 *
 * The magic carries the version, and the version is bumped whenever the layout changes instead of
 * carrying migration code for a build that has never shipped: a file from another version decodes as
 * "no stored credentials" and the user saves the password once more (`AGENTS.md`, and
 * `RelaxKonOS.Mobile.LoginCredentials.Design.md` §7.4).
 */
internal object VaultFileFormat {
    private const val MAGIC = 0x524B5632

    fun encode(records: List<VaultRecord>): ByteArray {
        val buffer = ByteArrayOutputStream()
        DataOutputStream(buffer).use { output ->
            output.writeInt(MAGIC)
            output.writeByte(records.first().kind.ordinal)
            output.writeInt(records.size)
            for (record in records) {
                output.writeUTF(record.serviceId)
                output.writeUTF(record.account)
                output.writeLong(record.lastUsedEpochMillis)
                output.writeByte(if (record.fingerprintProtected) 1 else 0)
                output.writeByte(record.state.ordinal)
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
                    val serviceId = input.readUTF()
                    val account = input.readUTF()
                    val lastUsed = input.readLong()
                    val protected = input.readByte().toInt() == 1
                    val state = VaultRecordState.entries[input.readByte().toInt()]
                    val iv = ByteArray(input.readInt()).also { input.readFully(it) }
                    val ciphertext = ByteArray(input.readInt()).also { input.readFully(it) }
                    records += VaultRecord(kind, serviceId, account, lastUsed, protected, iv, ciphertext, state)
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

/**
 * Maps Keystore failures onto the vault's own signal.
 *
 * The split matters now that an invalidated key only marks a record instead of deleting it: a failure
 * that merely means "not authorized right now" — the device is locked, or the window key has expired —
 * must not be reported as a dead key, because that would permanently brand a perfectly good record
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §4.1: unavailable is not invalidated).
 */
internal fun mapKeyException(kind: VaultKind, error: Throwable): Nothing {
    // The raw type and message are the only things that tell the four platform refusals apart, and
    // none of them is presentable to the user. Logged before the mapping discards the difference.
    VaultDiagnostics.failure("keystore.failure", error)
    when (error) {
        is VaultKeyInvalidatedException -> throw error
        is KeyPermanentlyInvalidatedException ->
            throw VaultKeyInvalidatedException("The ${kind.name} vault key was invalidated by a biometric change.", error)

        is UnrecoverableKeyException ->
            throw VaultKeyInvalidatedException("The ${kind.name} vault key can no longer be recovered.", error)

        is UserNotAuthenticatedException ->
            throw VaultKeyUnavailableException(
                "The ${kind.name} vault key needs a fresh authorization before it can be used.",
                error,
            )

        is GeneralSecurityException ->
            throw VaultKeyInvalidatedException("Keystore rejected the ${kind.name} vault key.", error)

        else -> throw error
    }
}
