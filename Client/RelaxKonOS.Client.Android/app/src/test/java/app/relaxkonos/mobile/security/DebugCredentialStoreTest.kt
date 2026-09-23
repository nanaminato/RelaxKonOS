package app.relaxkonos.mobile.security

import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The debug-only plaintext store (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §7.5).
 *
 * Two properties matter more than the round trip. It never holds more than one record — that is what
 * "only one" means structurally rather than by convention — and a file it cannot parse degrades to
 * "nothing stored" instead of blocking the sign-in screen, exactly as the credential vault does.
 *
 * One test asserts the opposite of what the vault's tests assert: the file *does* contain the
 * plaintext. That is the whole nature of this store, and pinning it stops anyone from reading the
 * class and assuming a protection that is not there.
 */
class DebugCredentialStoreTest {
    private val storage = InMemoryDebugCredentialStorage()
    private val store = DebugCredentialStore(storage)
    private val server = "http://192.168.1.10:5090"

    @Test
    fun `an empty store holds nothing`() {
        assertFalse(store.hasRecord())
        assertNull(store.record())
        assertNull(store.reveal(server, "nana"))
        assertFalse(store.exists(server, "nana"))
    }

    @Test
    fun `round trips a password`() {
        store.save(server, "nana", "correct horse battery staple".toCharArray())

        assertEquals("correct horse battery staple", String(store.reveal(server, "nana")!!))
    }

    @Test
    fun `round trips a non ascii password`() {
        store.save(server, "nana", "密码パスワード".toCharArray())

        assertEquals("密码パスワード", String(store.reveal(server, "nana")!!))
    }

    @Test
    fun `round trips an empty password`() {
        store.save(server, "nana", CharArray(0))

        assertArrayEquals(CharArray(0), store.reveal(server, "nana"))
    }

    @Test
    fun `the metadata carries the identity and no secret`() {
        store.save(server, "nana", "hunter2".toCharArray())

        assertEquals(DebugCredentialRecord(server, "nana"), store.record())
        assertTrue(store.exists(server, "nana"))
    }

    @Test
    fun `the file holds the plaintext password, which is the nature of this store`() {
        store.save(server, "nana", "hunter2".toCharArray())

        assertTrue(
            "A debug-only fallback for devices that cannot host a key is plaintext by definition.",
            String(storage.read()!!, Charsets.ISO_8859_1).contains("hunter2"),
        )
    }

    @Test
    fun `saving again replaces the single record`() {
        store.save(server, "nana", "first".toCharArray())
        store.save("https://other:5090", "root", "second".toCharArray())

        assertEquals(DebugCredentialRecord("https://other:5090", "root"), store.record())
        assertNull(store.reveal(server, "nana"))
        assertEquals("second", String(store.reveal("https://other:5090", "root")!!))
    }

    @Test
    fun `a read for another identity returns nothing`() {
        store.save(server, "nana", "hunter2".toCharArray())

        assertNull(store.reveal(server, "root"))
        assertNull(store.reveal("https://elsewhere:5090", "nana"))
        assertFalse(store.exists(server, "root"))
    }

    @Test
    fun `reveal hands back a copy the caller can zero`() {
        store.save(server, "nana", "hunter2".toCharArray())

        store.reveal(server, "nana")!!.fill('x')

        assertEquals("hunter2", String(store.reveal(server, "nana")!!))
    }

    @Test
    fun `delete only removes the matching identity`() {
        store.save(server, "nana", "hunter2".toCharArray())

        store.delete(server, "root")
        assertTrue(store.hasRecord())

        store.delete(server, "nana")
        assertFalse(store.hasRecord())
    }

    @Test
    fun `clear removes whatever is stored`() {
        store.save(server, "nana", "hunter2".toCharArray())

        store.clear()

        assertFalse(store.hasRecord())
        assertNull(store.reveal(server, "nana"))
    }

    @Test
    fun `a foreign file decodes as nothing`() {
        // The profile file's magic, written at this store's path: not ours, so not a credential.
        val foreign = ByteArrayOutputStream().also { buffer ->
            DataOutputStream(buffer).use { output ->
                output.writeInt(0x524B4332)
                output.writeUTF(server)
                output.writeUTF("nana")
                output.writeInt(6)
                output.write("hunter".toByteArray())
            }
        }
        storage.write(foreign.toByteArray())

        assertFalse(store.hasRecord())
        assertNull(store.reveal(server, "nana"))
    }

    @Test
    fun `a truncated file decodes as nothing`() {
        store.save(server, "nana", "hunter2".toCharArray())
        storage.write(storage.read()!!.copyOf(12))

        assertFalse(store.hasRecord())
        assertNull(store.reveal(server, "nana"))
    }
}
