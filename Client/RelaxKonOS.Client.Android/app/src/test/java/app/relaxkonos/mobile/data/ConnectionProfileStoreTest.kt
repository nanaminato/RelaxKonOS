package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.security.model.SavedLogin
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The saved login list holds no secret, but two properties matter
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §2.2, §6.3):
 *
 * - every mutation addresses one `(serverUrl, identifier)` pair, so one server's other accounts are
 *   never touched;
 * - the credential projection is a projection: it is reconciled against the vault rather than trusted.
 */
class ConnectionProfileStoreTest {
    private val storage = InMemoryProfileStorage()
    private val store = ConnectionProfileStore(storage)

    private val server = "https://alpha:5090"
    private val alpha = SavedLogin(server, "nana", 100L)
    private val otherAccount = SavedLogin(server, "root", 150L)
    private val beta = SavedLogin("https://beta:5090", "kana", 200L)

    @Test
    fun `starts empty`() {
        assertTrue(store.all().isEmpty())
        assertNull(store.recent())
    }

    @Test
    fun `an upserted login is readable again`() {
        store.upsert(alpha)

        assertEquals(listOf(alpha), store.all())
        assertEquals(alpha, store.recent())
    }

    @Test
    fun `the list is ordered by most recent use`() {
        store.upsert(alpha)
        store.upsert(beta)

        assertEquals(listOf(beta, alpha), store.all())
    }

    @Test
    fun `upserting the same server and account replaces the previous entry`() {
        store.upsert(alpha)
        store.upsert(alpha.copy(lastUsedEpochMillis = 500L))

        assertEquals(1, store.all().size)
        assertEquals(500L, store.all().first().lastUsedEpochMillis)
    }

    @Test
    fun `the same server with another account is a separate login`() {
        store.upsert(alpha)
        store.upsert(otherAccount)

        assertEquals(2, store.all().size)
    }

    @Test
    fun `removing a login leaves the other account on that server alone`() {
        store.upsert(alpha)
        store.upsert(otherAccount)
        store.upsert(beta)

        store.remove(server, "nana")

        assertEquals(listOf(beta, otherAccount), store.all())
    }

    @Test
    fun `removing an unknown login is harmless`() {
        store.upsert(alpha)

        store.remove("https://never-seen:5090", "nana")
        store.remove(server, "nobody")

        assertEquals(listOf(alpha), store.all())
    }

    @Test
    fun `forgetting a password keeps the login and clears the projection`() {
        store.upsert(alpha.copy(hasSavedCredential = true))

        store.setHasSavedCredential(server, "nana", false)

        assertEquals(1, store.all().size)
        assertFalse(store.all().first().hasSavedCredential)
    }

    @Test
    fun `one login's projection change does not touch another's`() {
        store.upsert(alpha.copy(hasSavedCredential = true))
        store.upsert(otherAccount.copy(hasSavedCredential = true))

        store.setHasSavedCredential(server, "nana", false)

        assertFalse(store.all().first { it.identifier == "nana" }.hasSavedCredential)
        assertTrue(store.all().first { it.identifier == "root" }.hasSavedCredential)
    }

    @Test
    fun `setting the projection of an unknown login writes nothing`() {
        val counting = CountingProfileStorage()
        val watched = ConnectionProfileStore(counting)
        watched.upsert(alpha)
        val writesAfterUpsert = counting.writes

        watched.setHasSavedCredential(server, "nobody", true)

        assertEquals(writesAfterUpsert, counting.writes)
    }

    @Test
    fun `reconciling follows the vault in both directions`() {
        store.upsert(alpha.copy(hasSavedCredential = true))
        store.upsert(beta)

        store.reconcileCredentialProjection { url, account -> url == beta.serverUrl && account == beta.identifier }

        assertEquals(
            listOf(beta.copy(hasSavedCredential = true), alpha.copy(hasSavedCredential = false)),
            store.all(),
        )
    }

    @Test
    fun `reconciling an already correct list does not rewrite the file`() {
        val counting = CountingProfileStorage()
        val watched = ConnectionProfileStore(counting)
        watched.upsert(alpha.copy(hasSavedCredential = true))
        val writesBefore = counting.writes

        watched.reconcileCredentialProjection { _, _ -> true }

        assertEquals(writesBefore, counting.writes)
    }

    @Test
    fun `a display name survives a round trip`() {
        store.upsert(alpha.copy(displayName = "Home lab"))

        assertEquals("Home lab", store.all().first().displayName)
    }

    @Test
    fun `an absent display name stays absent`() {
        store.upsert(alpha)

        assertNull(store.all().first().displayName)
    }

    @Test
    fun `a damaged file degrades to an empty list and stays usable`() {
        storage.write(byteArrayOf(9, 9, 9, 9))

        assertTrue(store.all().isEmpty())

        store.upsert(alpha)

        assertEquals(listOf(alpha), store.all())
    }

    @Test
    fun `a truncated file degrades to an empty list`() {
        store.upsert(alpha)
        val full = storage.read()!!
        storage.write(full.copyOfRange(0, full.size - 2))

        assertTrue(store.all().isEmpty())
    }

    /** Counts writes so a no-op reconcile can be proven not to touch the file. */
    private class CountingProfileStorage : ProfileStorage {
        var writes = 0
        private var payload: ByteArray? = null

        override fun read(): ByteArray? = payload?.copyOf()

        override fun write(payload: ByteArray) {
            writes++
            this.payload = payload.copyOf()
        }
    }
}
