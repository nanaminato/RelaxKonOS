package app.relaxkonos.mobile.data

import org.junit.Assert.*
import org.junit.Test

class TunnelMutationJournalTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    @Test fun `marker contains lookup identity only and survives journal reconstruction`() {
        val storage = Storage(); val entry = PendingTunnelMutation("host", "alice", TunnelMutation.SetToken, "profile", "profile")
        val journal = TunnelMutationJournal(storage); assertTrue(journal.begin(entry)); assertFalse(journal.begin(entry))
        val restored = TunnelMutationJournal(storage); assertFalse(restored.begin(entry)); restored.complete(entry)
        assertTrue(restored.begin(entry)); assertTrue(storage.bytes!!.size < 150)
    }
    @Test fun `different actor may have its own marker and malformed data cannot reset pending writes`() {
        val storage = Storage(); val journal = TunnelMutationJournal(storage)
        assertTrue(journal.begin(PendingTunnelMutation("host", "alice", TunnelMutation.Apply, "profile", "profile")))
        assertTrue(journal.begin(PendingTunnelMutation("host", "bob", TunnelMutation.Apply, "profile", "profile")))
        storage.bytes = byteArrayOf(1, 2, 3)
        assertTrue(runCatching { journal.begin(PendingTunnelMutation("host", "alice", TunnelMutation.Stop, "profile", "profile")) }.isFailure)
    }
}
