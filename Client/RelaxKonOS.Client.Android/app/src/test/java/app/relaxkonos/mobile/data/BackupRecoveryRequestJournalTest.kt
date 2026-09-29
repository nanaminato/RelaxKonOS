package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class BackupRecoveryRequestJournalTest {
    private class MemoryStorage : BackupRecoveryRequestStorage {
        var bytes: ByteArray? = null
        override fun read(): ByteArray? = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }

    private fun owner(service: String, account: String) = SessionState.Active(
        service, "https://example.test", account, "workspace", emptySet(), "linux",
        ExecutionEligibility(true, null, false),
    )

    @Test
    fun `pending key survives restart repeats for same owner and is isolated`() {
        val storage = MemoryStorage()
        val alice = owner("server-a", "alice")
        val first = BackupRecoveryRequestJournal(storage).begin(alice, "app-a")
        val restored = BackupRecoveryRequestJournal(storage)
        assertEquals(first, restored.pending(alice, "app-a"))
        assertEquals(first, restored.begin(alice, "app-a"))
        assertNull(restored.pending(owner("server-a", "bob"), "app-a"))
        assertNull(restored.pending(owner("server-b", "alice"), "app-a"))
        assertTrue(first.idempotencyKey.isNotBlank())
    }

    @Test
    fun `completion only removes the matching pending request`() {
        val storage = MemoryStorage()
        val journal = BackupRecoveryRequestJournal(storage)
        val alice = owner("server-a", "alice")
        val bob = owner("server-a", "bob")
        val appA = journal.begin(alice, "app-a")
        val appB = journal.begin(alice, "app-b")
        val bobA = journal.begin(bob, "app-a")
        journal.complete(alice, "app-a", appA.idempotencyKey)
        assertNull(journal.pending(alice, "app-a"))
        assertEquals(appB, journal.pending(alice, "app-b"))
        assertEquals(bobA, journal.pending(bob, "app-a"))
    }
}
