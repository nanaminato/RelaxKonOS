package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.security.model.SavedConnection
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The saved connection list holds no secret, but it must survive a damaged file and must not drift from
 * the vault: `all()` is the only ordering the UI relies on.
 */
class ConnectionProfileStoreTest {
    private val storage = InMemoryProfileStorage()
    private val store = ConnectionProfileStore(storage)

    private val alpha = SavedConnection("https://alpha:5090", "nana", 100L)
    private val beta = SavedConnection("https://beta:5090", "kana", 200L)

    @Test
    fun `starts empty`() {
        assertTrue(store.all().isEmpty())
        assertNull(store.recent())
    }

    @Test
    fun `an upserted connection is readable again`() {
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
    fun `the same server with another account is a separate connection`() {
        store.upsert(alpha)
        store.upsert(SavedConnection(alpha.serverUrl, "other", 300L))

        assertEquals(2, store.all().size)
    }

    @Test
    fun `remove drops the entries for that server`() {
        store.upsert(alpha)
        store.upsert(beta)

        store.remove(alpha.serverUrl)

        assertEquals(listOf(beta), store.all())
    }

    @Test
    fun `removing an unknown server is harmless`() {
        store.upsert(alpha)

        store.remove("https://never-seen:5090")

        assertEquals(listOf(alpha), store.all())
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
}
