package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.HostOperatingSystemKind
import app.relaxkonos.mobile.security.model.SavedLogin
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The saved login list holds no secret, but two properties matter
 * (`LoginCredentials.Design.md` §2.2, §6.3):
 *
 * - every mutation addresses one `(serviceId, identifier)` pair, so one server's other accounts are
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

        store.reconcileCredentialProjection { serviceId, account ->
            serviceId == beta.serviceId && account == beta.identifier
        }

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
    fun `managed login persists installation identity without a tunnel address`() {
        val managed = SavedLogin("rki-0123456789abcdef0123456789abcdef", "nana", 300L, "Home lab")

        store.upsert(managed)

        assertEquals(managed, store.all().single())
        assertNull(store.all().single().directServerUrl)
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

    @Test
    fun `a host operating system survives a round trip`() {
        store.upsert(alpha.copy(hostOperatingSystem = HostOperatingSystemKind.WindowsServer))

        assertEquals(HostOperatingSystemKind.WindowsServer, store.all().single().hostOperatingSystem)
    }

    @Test
    fun `a login nobody has asked keeps no host answer`() {
        store.upsert(alpha)

        assertNull(store.all().single().hostOperatingSystem)
    }

    @Test
    fun `storing a host answer updates every account on the same service`() {
        store.upsert(alpha)
        store.upsert(otherAccount)

        assertTrue(store.setHostOperatingSystem(server, HostOperatingSystemKind.Ubuntu))

        assertEquals(HostOperatingSystemKind.Ubuntu, store.all().first { it.identifier == "nana" }.hostOperatingSystem)
        assertEquals(HostOperatingSystemKind.Ubuntu, store.all().first { it.identifier == "root" }.hostOperatingSystem)
    }

    @Test
    fun `storing the same host answer again does not rewrite the file`() {
        val counting = CountingProfileStorage()
        val watched = ConnectionProfileStore(counting)
        watched.upsert(alpha.copy(hostOperatingSystem = HostOperatingSystemKind.Ubuntu))
        val writesBefore = counting.writes

        assertFalse(watched.setHostOperatingSystem(server, HostOperatingSystemKind.Ubuntu))

        assertEquals(writesBefore, counting.writes)
    }

    @Test
    fun `a host answer for a login that is gone writes nothing`() {
        val counting = CountingProfileStorage()
        val watched = ConnectionProfileStore(counting)
        watched.upsert(alpha)
        val writesAfterUpsert = counting.writes

        assertFalse(watched.setHostOperatingSystem("https://gone:5090", HostOperatingSystemKind.Ubuntu))

        assertEquals(writesAfterUpsert, counting.writes)
    }

    @Test
    fun `a file written by the previous layout is discarded rather than half-read`() {
        // `RKC2` had no host column. Reading it as if it had one would misplace every byte after the
        // first record, so the layout rule is "a version this build does not know means empty" — and
        // this is the row that keeps that honest.
        val previous = ByteArrayOutputStream()
        DataOutputStream(previous).use { output ->
            output.writeInt(0x524B4332)
            output.writeInt(1)
            output.writeUTF(server)
            output.writeUTF("nana")
            output.writeLong(100L)
            output.writeByte(0)
            output.writeByte(1)
        }
        storage.write(previous.toByteArray())

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
