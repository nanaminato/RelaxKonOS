package app.relaxkonos.mobile.data

import org.junit.Assert.assertEquals
import org.junit.Test

class RecentOperationJournalTest {
    @Test
    fun `new records are first and journal retains only its capacity`() {
        val journal = RecentOperationJournal(capacity = 2)

        journal.record(RecentOperationKind.Upload, "first", 1)
        journal.record(RecentOperationKind.Copy, "second", 2)
        journal.record(RecentOperationKind.Delete, "third", 3)

        assertEquals(
            listOf(
                RecentOperation(RecentOperationKind.Delete, "third", 3),
                RecentOperation(RecentOperationKind.Copy, "second", 2),
            ),
            journal.entries.value,
        )
    }
}
