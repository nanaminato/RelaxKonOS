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
    fun `a free name is kept as it is`() {
        assertEquals("report.txt", uniqueDownloadName("report.txt") { false })
    }

    @Test
    fun `a taken name gains a counter before the extension`() {
        val taken = setOf("report.txt", "report (1).txt")
        assertEquals("report (2).txt", uniqueDownloadName("report.txt") { it in taken })
    }

    @Test
    fun `a name without an extension gains the counter at the end`() {
        val taken = setOf("notes")
        assertEquals("notes (1)", uniqueDownloadName("notes") { it in taken })
    }

    @Test
    fun `only the last extension is treated as one`() {
        val taken = setOf("archive.tar.gz")
        assertEquals("archive.tar (1).gz", uniqueDownloadName("archive.tar.gz") { it in taken })
    }

    @Test
    fun `a dotfile keeps its leading dot`() {
        val taken = setOf(".bashrc")
        assertEquals(".bashrc (1)", uniqueDownloadName(".bashrc") { it in taken })
    }

    @Test
    fun `a server-provided name is reduced to its last path component`() {
        assertEquals("report.txt", downloadFileName("C:\\work/report.txt"))
        assertEquals("report.txt", downloadFileName("/srv/drop/report.txt"))
        assertEquals("report.txt", downloadFileName("report.txt"))
    }
}
