package app.relaxkonos.mobile.data

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The name rules a download follows on the way into the shared Downloads collection.
 *
 * Only the parts that do not touch `MediaStore` are covered here — the store itself is `ContentResolver`
 * work, which a JVM test cannot execute. What is testable is exactly what used to be wrong: the name a
 * server hands over is a file name, and a download never overwrites an existing file.
 */
class DownloadStoreTest {
    @Test
    fun `a server-provided name is reduced to its last path component`() {
        assertEquals("report.txt", downloadFileName("C:\\work/report.txt"))
        assertEquals("report.txt", downloadFileName("/srv/drop/report.txt"))
        assertEquals("report.txt", downloadFileName("report.txt"))
    }
}
