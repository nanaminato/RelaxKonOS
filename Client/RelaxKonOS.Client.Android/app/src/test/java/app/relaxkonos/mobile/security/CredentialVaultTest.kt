package app.relaxkonos.mobile.security

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream

/**
 * The invariants of `RelaxKonOS.Mobile.V1.Design.md` §5.3 that the vault itself must hold: payloads are
 * bound to their record identity, the two vaults stay separate, and the password never appears in the
 * stored bytes.
 */
class CredentialVaultTest {
    private val storage = InMemoryVaultStorage()
    private val vault = CredentialVault(storage, FakeVaultCrypto())
    private val server = "https://relaxkonos.local:5090"

    private fun seal(kind: VaultKind, account: String, password: String, url: String = server): VaultRecord = vault.seal(
        kind = kind,
        serverUrl = url,
        account = account,
        password = password.toCharArray(),
        cipher = vault.beginSeal(kind),
        fingerprintProtected = true,
        nowEpochMillis = 1_000L,
    )

    private fun open(record: VaultRecord): String = String(vault.open(record, vault.beginOpen(record)))

    @Test
    fun `round trips a password`() {
        assertEquals("correct horse battery staple", open(seal(VaultKind.Connection, "nana", "correct horse battery staple")))
    }

    @Test
    fun `round trips non ascii characters`() {
        val password = "密码パスワード"
        assertEquals(password, open(seal(VaultKind.Connection, "nana", password)))
    }

    @Test
    fun `utf8 helpers round trip a surrogate pair`() {
        val value = "🔐 pass".toCharArray()
        assertArrayEquals(value, decodeUtf8(encodeUtf8(value)))
    }

    @Test
    fun `the stored bytes never contain the password`() {
        val password = "correct horse battery staple"
        seal(VaultKind.Connection, "nana", password)

        val stored = storage.read(VaultKind.Connection)
        assertNotNull(stored)
        assertFalse(
            "The ciphertext must not contain the plaintext password.",
            String(stored!!, Charsets.ISO_8859_1).contains(password),
        )
    }

    @Test
    fun `a payload moved to another server is refused`() {
        val sealed = seal(VaultKind.Connection, "nana", "hunter2")
        val moved = VaultRecord(
            kind = VaultKind.Connection,
            serverUrl = "https://elsewhere:5090",
            account = sealed.account,
            lastUsedEpochMillis = sealed.lastUsedEpochMillis,
            fingerprintProtected = sealed.fingerprintProtected,
            iv = sealed.iv,
            ciphertext = sealed.ciphertext,
        )
        assertThrows(VaultTamperException::class.java) { open(moved) }
    }

    @Test
    fun `a payload moved to another account is refused`() {
        val sealed = seal(VaultKind.Connection, "nana", "hunter2")
        val moved = VaultRecord(
            kind = VaultKind.Connection,
            serverUrl = sealed.serverUrl,
            account = "someone-else",
            lastUsedEpochMillis = sealed.lastUsedEpochMillis,
            fingerprintProtected = sealed.fingerprintProtected,
            iv = sealed.iv,
            ciphertext = sealed.ciphertext,
        )
        assertThrows(VaultTamperException::class.java) { open(moved) }
    }

    @Test
    fun `a connection payload cannot be opened as an elevation credential`() {
        val sealed = seal(VaultKind.Connection, "nana", "hunter2")
        val relabelled = VaultRecord(
            kind = VaultKind.Elevation,
            serverUrl = sealed.serverUrl,
            account = sealed.account,
            lastUsedEpochMillis = sealed.lastUsedEpochMillis,
            fingerprintProtected = sealed.fingerprintProtected,
            iv = sealed.iv,
            ciphertext = sealed.ciphertext,
        )
        assertThrows(VaultTamperException::class.java) { open(relabelled) }
    }

    @Test
    fun `records of the same vault stay independent`() {
        val first = seal(VaultKind.Connection, "nana", "first")
        val second = seal(VaultKind.Connection, "kana", "second")

        assertEquals("first", open(vault.record(VaultKind.Connection, server, "nana")!!))
        assertEquals("second", open(vault.record(VaultKind.Connection, server, "kana")!!))
        assertArrayEquals(first.ciphertext, vault.record(VaultKind.Connection, server, "nana")!!.ciphertext)
        assertArrayEquals(second.ciphertext, vault.record(VaultKind.Connection, server, "kana")!!.ciphertext)
    }

    @Test
    fun `the two vaults do not see each other`() {
        seal(VaultKind.Connection, "nana", "server-password")
        seal(VaultKind.Elevation, "admin", "administrator-password")

        assertNull(vault.record(VaultKind.Connection, server, "admin"))
        assertNull(vault.record(VaultKind.Elevation, server, "nana"))
        assertEquals(1, vault.records(VaultKind.Connection).size)
        assertEquals(1, vault.records(VaultKind.Elevation).size)
        assertEquals("administrator-password", open(vault.record(VaultKind.Elevation, server, "admin")!!))
    }

    @Test
    fun `sealing the same record twice replaces it`() {
        seal(VaultKind.Connection, "nana", "old")
        seal(VaultKind.Connection, "nana", "new")

        assertEquals(1, vault.records(VaultKind.Connection).size)
        assertEquals("new", open(vault.record(VaultKind.Connection, server, "nana")!!))
    }

    @Test
    fun `delete removes exactly one record`() {
        seal(VaultKind.Connection, "nana", "first")
        seal(VaultKind.Connection, "kana", "second")

        vault.delete(VaultKind.Connection, server, "nana")

        assertNull(vault.record(VaultKind.Connection, server, "nana"))
        assertNotNull(vault.record(VaultKind.Connection, server, "kana"))
    }

    @Test
    fun `deleting the last record removes the file`() {
        seal(VaultKind.Connection, "nana", "first")
        vault.delete(VaultKind.Connection, server, "nana")

        assertNull(storage.read(VaultKind.Connection))
        assertFalse(vault.hasRecords(VaultKind.Connection))
    }

    @Test
    fun `clear removes one vault and leaves the other alone`() {
        seal(VaultKind.Connection, "nana", "server-password")
        seal(VaultKind.Elevation, "admin", "administrator-password")

        vault.clear(VaultKind.Connection)

        assertTrue(vault.records(VaultKind.Connection).isEmpty())
        assertEquals(1, vault.records(VaultKind.Elevation).size)
    }

    @Test
    fun `clearRecords keeps the other records of the same vault`() {
        seal(VaultKind.Connection, "nana", "first")
        seal(VaultKind.Connection, "kana", "second")

        vault.clearRecords(VaultKind.Connection, listOf(vault.record(VaultKind.Connection, server, "nana")!!))

        assertNull(vault.record(VaultKind.Connection, server, "nana"))
        assertNotNull(vault.record(VaultKind.Connection, server, "kana"))
    }

    @Test
    fun `markUsed updates the stamp without disturbing the payload`() {
        seal(VaultKind.Connection, "nana", "hunter2")
        val before = vault.record(VaultKind.Connection, server, "nana")!!

        vault.markUsed(before, 9_000L)

        val after = vault.record(VaultKind.Connection, server, "nana")!!
        assertEquals(9_000L, after.lastUsedEpochMillis)
        assertArrayEquals(before.ciphertext, after.ciphertext)
        assertEquals("hunter2", open(after))
    }

    @Test
    fun `markUsed ignores a record that is no longer stored`() {
        seal(VaultKind.Connection, "nana", "hunter2")
        val record = vault.record(VaultKind.Connection, server, "nana")!!
        vault.delete(VaultKind.Connection, server, "nana")

        vault.markUsed(record, 9_000L)

        assertNull(vault.record(VaultKind.Connection, server, "nana"))
    }

    @Test
    fun `a vault file from another version degrades to no credentials`() {
        storage.write(VaultKind.Connection, byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8))

        assertTrue(vault.records(VaultKind.Connection).isEmpty())
    }

    /**
     * The layout gained a state byte per record, so the version moved to `RKV2` and a file written by the
     * previous layout is not migrated: it reads as "no stored credentials" and the user saves once more
     * (`AGENTS.md`: no compatibility shims before the first release).
     */
    @Test
    fun `a vault file from the previous layout degrades to no credentials`() {
        val legacy = ByteArrayOutputStream()
        DataOutputStream(legacy).use { output ->
            output.writeInt(0x524B5631)
            output.writeByte(VaultKind.Connection.ordinal)
            output.writeInt(0)
        }
        storage.write(VaultKind.Connection, legacy.toByteArray())

        assertTrue(vault.records(VaultKind.Connection).isEmpty())
    }

    @Test
    fun `a truncated vault file degrades to no credentials`() {
        seal(VaultKind.Connection, "nana", "hunter2")
        val full = storage.read(VaultKind.Connection)!!
        storage.write(VaultKind.Connection, full.copyOfRange(0, full.size - 3))

        assertTrue(vault.records(VaultKind.Connection).isEmpty())
    }

    @Test
    fun `markInvalidated keeps the record and its ciphertext but refuses to read it`() {
        val sealed = seal(VaultKind.Connection, "nana", "hunter2")

        vault.markInvalidated(sealed)

        val kept = vault.record(VaultKind.Connection, server, "nana")!!
        assertEquals(VaultRecordState.Invalidated, kept.state)
        assertArrayEquals(sealed.iv, kept.iv)
        assertArrayEquals(sealed.ciphertext, kept.ciphertext)
        // Both gates refuse: building a Cipher for a dead key, and decrypting with one already built.
        assertThrows(VaultRecordInvalidatedException::class.java) { vault.beginOpen(kept) }
        assertThrows(VaultRecordInvalidatedException::class.java) { vault.open(kept, vault.beginOpen(sealed)) }
    }

    @Test
    fun `markInvalidated leaves the other records of the same vault sealed`() {
        seal(VaultKind.Connection, "nana", "first")
        val target = seal(VaultKind.Connection, "kana", "second")

        vault.markInvalidated(target)

        assertEquals(VaultRecordState.Sealed, vault.record(VaultKind.Connection, server, "nana")!!.state)
        assertEquals(VaultRecordState.Invalidated, vault.record(VaultKind.Connection, server, "kana")!!.state)
        assertEquals("first", open(vault.record(VaultKind.Connection, server, "nana")!!))
    }

    @Test
    fun `markInvalidated ignores a record that is no longer stored`() {
        val sealed = seal(VaultKind.Connection, "nana", "hunter2")
        vault.delete(sealed)

        vault.markInvalidated(sealed)

        assertNull(vault.record(VaultKind.Connection, server, "nana"))
    }

    @Test
    fun `saving again replaces an invalidated record with a sealed one`() {
        val sealed = seal(VaultKind.Connection, "nana", "old")
        vault.markInvalidated(sealed)

        val replacement = seal(VaultKind.Connection, "nana", "new")

        assertEquals(VaultRecordState.Sealed, replacement.state)
        assertEquals(1, vault.records(VaultKind.Connection).size)
        assertEquals("new", open(vault.record(VaultKind.Connection, server, "nana")!!))
    }

    @Test
    fun `the invalidated state survives a reload of the vault file`() {
        vault.markInvalidated(seal(VaultKind.Connection, "nana", "hunter2"))

        val reloaded = CredentialVault(storage, FakeVaultCrypto())
            .record(VaultKind.Connection, server, "nana")!!

        assertEquals(VaultRecordState.Invalidated, reloaded.state)
    }

    @Test
    fun `markUsed does not resurrect an invalidated record`() {
        vault.markInvalidated(seal(VaultKind.Connection, "nana", "hunter2"))

        vault.markUsed(vault.record(VaultKind.Connection, server, "nana")!!, 9_000L)

        val after = vault.record(VaultKind.Connection, server, "nana")!!
        assertEquals(VaultRecordState.Invalidated, after.state)
        assertEquals(9_000L, after.lastUsedEpochMillis)
    }
}
