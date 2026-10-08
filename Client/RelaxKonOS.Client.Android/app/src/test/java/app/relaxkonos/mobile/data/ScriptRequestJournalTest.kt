package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import org.junit.Assert.*
import org.junit.Test

class ScriptRequestJournalTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val storage = Storage()
    private val owner = SessionState.Active("https://host", "https://host", "alice", "Workspace", emptySet(), "linux",
        ExecutionEligibility(true, null, false), workspaceId = "workspace")
    private val pending = PendingScriptRequest("11111111-1111-1111-1111-111111111111", false)

    @Test fun `recreated journal preserves only scoped lookup ID and operation type`() {
        ScriptRequestJournal(storage).begin(owner, pending)
        val journal = ScriptRequestJournal(storage)
        assertEquals(pending, journal.pending(owner))
        assertNull(journal.pending(owner.copy(userName = "bob")))
        assertNull(journal.pending(owner.copy(serviceId = "https://other")))
        val cancellation = PendingScriptRequest("22222222-2222-2222-2222-222222222222", true)
        journal.begin(owner.copy(userName = "bob"), cancellation)
        assertEquals(cancellation, ScriptRequestJournal(storage).pending(owner.copy(userName = "bob")))
        journal.complete(owner, pending)
        assertNull(journal.pending(owner))
        assertEquals(cancellation, journal.pending(owner.copy(userName = "bob")))
    }

    @Test fun `pending request cannot be replaced or cleared by another ID or kind`() {
        val journal = ScriptRequestJournal(storage); journal.begin(owner, pending)
        assertTrue(runCatching { journal.begin(owner, pending.copy(taskId = "other")) }.isFailure)
        assertTrue(runCatching { journal.complete(owner, pending.copy(cancellation = true)) }.isFailure)
        assertTrue(runCatching { journal.complete(owner, pending.copy(taskId = "other")) }.isFailure)
        assertEquals(pending, journal.pending(owner))
    }

    @Test fun `truncated or trailing corrupt bytes fail closed`() {
        val journal = ScriptRequestJournal(storage); journal.begin(owner, pending)
        val original = requireNotNull(storage.bytes)
        storage.bytes = original.dropLast(1).toByteArray()
        assertTrue(runCatching { journal.pending(owner) }.isFailure)
        storage.bytes = original + byteArrayOf(0)
        assertTrue(runCatching { journal.pending(owner) }.isFailure)
        assertTrue(runCatching { journal.begin(owner, pending) }.isFailure)
    }

    @Test fun `invalid lookup IDs cannot enter journal`() {
        val journal = ScriptRequestJournal(storage)
        for (id in listOf("", " ", "a\u0000b", "x".repeat(129)))
            assertTrue(runCatching { journal.begin(owner, pending.copy(taskId = id)) }.isFailure)
        assertNull(storage.bytes)
    }

    @Test fun `send marker survives recreation and stale unsent entry cannot clear it`() {
        val journal = ScriptRequestJournal(storage); journal.begin(owner, pending)
        assertFalse(requireNotNull(journal.pending(owner)).attempted)
        val attempted = journal.attempted(owner, pending)
        assertTrue(attempted.attempted)
        assertEquals(attempted, ScriptRequestJournal(storage).pending(owner))
        assertTrue(runCatching { journal.complete(owner, pending) }.isFailure)
        assertTrue(runCatching { journal.attempted(owner, attempted) }.isFailure)
        journal.complete(owner, attempted)
        assertNull(journal.pending(owner))
    }

    @Test fun `obsolete format is rejected without a fallback parser`() {
        ScriptRequestJournal(storage).begin(owner, pending)
        val bytes = requireNotNull(storage.bytes).copyOf()
        bytes[3] = 0x31
        storage.bytes = bytes
        assertTrue(runCatching { ScriptRequestJournal(storage).pending(owner) }.isFailure)
        assertTrue(runCatching { ScriptRequestJournal(storage).begin(owner, pending) }.isFailure)
    }
}
