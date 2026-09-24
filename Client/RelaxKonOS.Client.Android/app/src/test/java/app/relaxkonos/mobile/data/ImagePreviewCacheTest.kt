package app.relaxkonos.mobile.data

import java.io.File
import java.util.UUID
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The preview cache: how an entry is identified, when it may be reused, and what gets deleted.
 *
 * All of it runs against a real directory in the host's temporary folder — the store is `java.io` and
 * nothing else, so unlike `DownloadStore` (which is `ContentResolver` work a JVM test cannot execute)
 * the interesting half is testable here. What is *not* testable is the decode itself; that is why
 * `previewSampleSize` is a pure function with its own tests in `ImageDecoderTest`.
 */
class ImagePreviewCacheTest {
    private val directory = File(
        System.getProperty("java.io.tmpdir"),
        "relaxkonos-preview-${UUID.randomUUID()}",
    )

    private val cache = ImagePreviewCache(directory)

    @After
    fun cleanUp() {
        directory.deleteRecursively()
    }

    // ---- Identity ------------------------------------------------------

    @Test
    fun `the same revision on the same server is one entry`() {
        assertEquals(
            previewCacheFileName("https://host", "C:\\pics\\a.png", 10, 20),
            previewCacheFileName("https://host", "C:\\pics\\a.png", 10, 20),
        )
    }

    @Test
    fun `a changed file is a different entry`() {
        val original = previewCacheFileName("https://host", "C:\\pics\\a.png", 10, 20)
        assertFalse(original == previewCacheFileName("https://host", "C:\\pics\\a.png", 11, 20))
        assertFalse(original == previewCacheFileName("https://host", "C:\\pics\\a.png", 10, 21))
    }

    @Test
    fun `another host is a different entry`() {
        assertFalse(
            previewCacheFileName("https://one", "C:\\pics\\a.png", 10, 20) ==
                previewCacheFileName("https://two", "C:\\pics\\a.png", 10, 20),
        )
    }

    @Test
    fun `a remote path becomes a name a file system accepts`() {
        val name = previewCacheFileName("https://host", "C:\\Users\\b\\Desktop\\a:b.png", 10, 20)
        assertTrue(name.endsWith(".preview"))
        assertFalse(name.contains('/'))
        assertFalse(name.contains('\\'))
        assertFalse(name.contains(':'))
    }

    // ---- Reuse ---------------------------------------------------------

    @Test
    fun `nothing is cached before anything is fetched`() {
        assertNull(cache.cached("https://host", "C:\\pics\\a.png", 10, 20))
    }

    @Test
    fun `a complete copy is reused`() {
        val bytes = byteArrayOf(1, 2, 3, 4)
        cache.create("https://host", "C:\\pics\\a.png", bytes.size.toLong(), 20).file.writeBytes(bytes)

        assertEquals(bytes.size.toLong(), cache.cached("https://host", "C:\\pics\\a.png", 4, 20)?.length())
    }

    @Test
    fun `a copy cut short is not reused`() {
        // What a transfer interrupted by a crash leaves behind: a file at the right name and the wrong
        // length. Reusing it would draw half a picture and call it the user's file.
        cache.create("https://host", "C:\\pics\\a.png", 4096, 20).file.writeBytes(byteArrayOf(1, 2, 3))

        assertNull(cache.cached("https://host", "C:\\pics\\a.png", 4096, 20))
    }

    @Test
    fun `an empty copy is not reused`() {
        cache.create("https://host", "C:\\pics\\a.png", null, 20).file.writeBytes(ByteArray(0))

        assertNull(cache.cached("https://host", "C:\\pics\\a.png", null, 20))
    }

    @Test
    fun `a copy of an unannounced length is reused when it has bytes`() {
        cache.create("https://host", "C:\\pics\\a.png", null, 20).file.writeBytes(byteArrayOf(1, 2))

        assertNotNull(cache.cached("https://host", "C:\\pics\\a.png", null, 20))
    }

    // ---- Eviction ------------------------------------------------------

    @Test
    fun `a cache inside its budget loses nothing`() {
        val entries = listOf(PreviewEntry("a", 10, 1), PreviewEntry("b", 10, 2))
        assertEquals(emptyList<PreviewEntry>(), previewsToEvict(entries, 100))
    }

    @Test
    fun `a cache over its budget loses its least recently used entries first`() {
        val entries = listOf(
            PreviewEntry("newest", 10, 3),
            PreviewEntry("oldest", 10, 1),
            PreviewEntry("middle", 10, 2),
        )
        assertEquals(listOf("oldest"), previewsToEvict(entries, 20).map { it.name })
    }

    @Test
    fun `eviction stops as soon as the budget is met`() {
        val entries = listOf(
            PreviewEntry("a", 40, 1),
            PreviewEntry("b", 40, 2),
            PreviewEntry("c", 40, 3),
            PreviewEntry("d", 40, 4),
        )
        // 160 bytes against a 100-byte budget: dropping the two oldest leaves 80.
        assertEquals(listOf("a", "b"), previewsToEvict(entries, 100).map { it.name })
    }

    @Test
    fun `one entry larger than the whole budget is evicted`() {
        val entries = listOf(PreviewEntry("huge", 500, 1), PreviewEntry("small", 10, 2))
        assertEquals(listOf("huge"), previewsToEvict(entries, 100).map { it.name })
    }

    @Test
    fun `trim deletes the files the policy chose`() {
        val kept = cache.create("https://host", "C:\\pics\\kept.png", 10, 2).file
        val dropped = cache.create("https://host", "C:\\pics\\dropped.png", 10, 1).file
        kept.writeBytes(ByteArray(10))
        dropped.writeBytes(ByteArray(10))
        dropped.setLastModified(1_000L)
        kept.setLastModified(2_000L)

        cache.trim(budgetBytes = 10)

        assertTrue(kept.isFile)
        assertFalse(dropped.isFile)
    }

    @Test
    fun `trim leaves files it does not own alone`() {
        directory.mkdirs()
        val foreign = File(directory, "something-else.txt").apply { writeBytes(ByteArray(4096)) }

        cache.trim(budgetBytes = 0)

        assertTrue(foreign.isFile)
    }
}
