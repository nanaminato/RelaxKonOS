package app.relaxkonos.mobile.data

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/** Server / user / workspace / device, the key an entry is scoped to. */
private const val SERVER = "https://relaxkonos.local|nana|studio|device"

/**
 * The resume journal is the only thing that survives a process kill, so its format is a contract with
 * the next launch rather than an implementation detail: every field has to come back, a record it cannot
 * read has to be skipped instead of fatal, and the file has to stay bounded.
 */
class UploadResumeJournalTest {
    @get:Rule
    val folder = TemporaryFolder()

    private var now = 1_000_000L

    private fun journal() = UploadResumeJournal(File(folder.root, "upload-resume.txt")) { now }

    private fun entry(
        uploadId: String = "session-1",
        serverKey: String = SERVER,
        sourceKey: String = "content://docs/1|4096|17",
        confirmedOffset: Long = 1_048_576L,
        recordedAtMillis: Long = 1_000L,
    ) = UploadResumeEntry(
        serverKey = serverKey,
        uploadId = uploadId,
        // A Windows path and a name with the two characters that would break a naive separator: the
        // journal is a line-per-record text format, so both have to survive a round trip verbatim.
        targetDirectoryPath = "C:\\srv\\media\tv",
        fileName = "holiday\nvideo \"final\".mp4",
        sourceUri = "content://docs/1",
        sourceKey = sourceKey,
        confirmedOffset = confirmedOffset,
        totalLength = 900_000_000L,
        recordedAtMillis = recordedAtMillis,
    )

    @Test
    fun `every field survives a round trip, including a path with separators in it`() {
        val journal = journal()
        val written = entry()
        journal.record(written)

        val read = journal.entries(SERVER).single()

        assertEquals(written, read)
    }

    @Test
    fun `a fresh journal file is created for the first record`() {
        val file = File(folder.root, "nested/upload-resume.txt")
        val journal = UploadResumeJournal(file) { now }

        journal.record(entry())

        assertTrue(file.exists())
        assertEquals(1, journal.entries(SERVER).size)
    }

    @Test
    fun `re-recording the same session replaces its entry instead of appending a second one`() {
        val journal = journal()
        journal.record(entry(confirmedOffset = 1_048_576L))
        journal.record(entry(confirmedOffset = 8_388_608L))

        val entries = journal.entries(SERVER)

        assertEquals(1, entries.size)
        assertEquals(8_388_608L, entries.single().confirmedOffset)
    }

    @Test
    fun `entries come back newest first, which is the order the unfinished list shows`() {
        val journal = journal()
        journal.record(entry(uploadId = "old", recordedAtMillis = 1_000L))
        journal.record(entry(uploadId = "new", recordedAtMillis = 2_000L))
        journal.record(entry(uploadId = "middle", recordedAtMillis = 1_500L))

        assertEquals(listOf("new", "middle", "old"), journal.entries(SERVER).map { it.uploadId })
    }

    @Test
    fun `only the current server's entries are listed`() {
        val journal = journal()
        journal.record(entry(uploadId = "mine", serverKey = SERVER))
        journal.record(entry(uploadId = "theirs", serverKey = "other-server|bob|studio|device"))

        assertEquals(listOf("mine"), journal.entries(SERVER).map { it.uploadId })
    }

    @Test
    fun `an entry older than the server would keep its session is dropped on read`() {
        val journal = journal()
        now = 2_000_000L
        journal.record(entry(uploadId = "ancient", recordedAtMillis = now))
        journal.record(entry(uploadId = "recent", recordedAtMillis = now + 1))

        // The clock moves on while the app is not running: this is a launch a week and a day later, with
        // no write in between. A session the server swept cannot be resumed, so offering it is a lie.
        now += UploadResumeJournal.MAXIMUM_AGE_MILLIS + 1

        assertEquals(listOf("recent"), journal.entries(SERVER).map { it.uploadId })
    }

    @Test
    fun `the oldest entries are dropped once the file is full`() {
        val journal = journal()
        repeat(UploadResumeJournal.MAXIMUM_ENTRIES + 4) { index ->
            journal.record(entry(uploadId = "session-$index", recordedAtMillis = 1_000L + index))
        }

        val entries = journal.entries(SERVER)

        assertEquals(UploadResumeJournal.MAXIMUM_ENTRIES, entries.size)
        // The four oldest went; the newest is still there.
        assertEquals("session-${UploadResumeJournal.MAXIMUM_ENTRIES + 3}", entries.first().uploadId)
        assertTrue(entries.none { it.uploadId == "session-0" })
    }

    @Test
    fun `removing an entry returns it so its cache copy can go with it`() {
        val journal = journal()
        journal.record(entry(uploadId = "a"))
        journal.record(entry(uploadId = "b"))

        val removed = journal.remove("a")

        assertEquals("a", removed?.uploadId)
        assertEquals(listOf("b"), journal.entries(SERVER).map { it.uploadId })
        assertNull(journal.remove("a"))
    }

    @Test
    fun `keeping one server drops the others and hands them back`() {
        val journal = journal()
        journal.record(entry(uploadId = "mine", serverKey = SERVER))
        journal.record(entry(uploadId = "theirs", serverKey = "other-server|bob|studio|device"))

        val dropped = journal.keepServer(SERVER)

        assertEquals(listOf("theirs"), dropped.map { it.uploadId })
        assertEquals(listOf("mine"), journal.entries(SERVER).map { it.uploadId })
        assertTrue(journal.entries("other-server|bob|studio|device").isEmpty())
    }

    @Test
    fun `finding by document removes a stale session for the same document`() {
        val journal = journal()
        val sourceKey = "content://docs/1|4096|17"
        journal.record(entry(uploadId = "stale", sourceKey = sourceKey))
        journal.record(entry(uploadId = "current", sourceKey = sourceKey))

        val found = journal.find(SERVER, sourceKey, 900_000_000L)

        assertEquals("current", found?.uploadId)
        // The superseded attempt would never be resumed, so leaving it would keep its cache copy forever.
        assertEquals(listOf("current"), journal.entries(SERVER).map { it.uploadId })
    }

    @Test
    fun `finding a document the same server has never uploaded returns nothing`() {
        val journal = journal()
        journal.record(entry(sourceKey = "content://docs/1|4096|17"))

        assertNull(journal.find(SERVER, "content://docs/2|8192|18", 900_000_000L))
        assertNull(journal.find("other-server|bob|studio|device", "content://docs/1|4096|17", 900_000_000L))
        assertNull(journal.find(SERVER, "content://docs/1|4096|17", 1L))
    }

    @Test
    fun `a file that is not ours is ignored rather than parsed`() {
        File(folder.root, "upload-resume.txt").writeText("something else entirely\n1\t2\t3\n")

        assertTrue(journal().entries(SERVER).isEmpty())
    }

    @Test
    fun `a truncated or unreadable line is skipped and the rest still loads`() {
        val journal = journal()
        journal.record(entry(uploadId = "keeper"))
        val file = File(folder.root, "upload-resume.txt")
        file.writeText(
            file.readText() +
                "not-a-timestamp\tserver\tbroken\t\t\t\t\tnot-a-number\t0\n" +
                "1700000000000\t$SERVER\ttruncated\tC:\\srv\n",
        )

        assertEquals(listOf("keeper"), journal.entries(SERVER).map { it.uploadId })
    }
}
