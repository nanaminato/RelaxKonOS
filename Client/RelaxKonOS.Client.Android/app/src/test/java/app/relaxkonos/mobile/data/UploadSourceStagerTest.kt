package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.UploadProtocol
import java.io.ByteArrayInputStream
import java.io.File
import java.io.FileInputStream
import java.io.IOException
import java.io.InputStream
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * The stager decides whether a document can be read from an offset, and that decision is the whole
 * technical prerequisite of resumption: everything downstream assumes `openAt` works. It is also the only
 * place a large document can be copied, so its space guard and its cache/entry pairing are load-bearing.
 */
class UploadSourceStagerTest {
    @get:Rule
    val folder = TemporaryFolder()

    /** A computed path, not a stored one: the rule's folder does not exist yet during construction. */
    private val cacheRoot get() = File(folder.root, "cache/uploads")

    private var free = Long.MAX_VALUE / 2

    private fun stager() = UploadSourceStager(cacheRoot) { free }

    private fun payload(name: String = "video.mp4", bytes: ByteArray = ByteArray(4_096) { it.toByte() }): File =
        File(folder.root, name).apply { writeBytes(bytes) }

    /** A document backed by a real file: a provider that can be positioned. */
    private fun seekable(file: File) = PickedDocument(
        uri = "content://docs/${file.name}",
        displayName = file.name,
        length = file.length(),
        lastModifiedMillis = file.lastModified(),
        open = { FileInputStream(file) },
        openAt = { offset -> FileInputStream(file).also { it.channel.position(offset) } },
    )

    /**
     * A document backed by a pipe: what the picker hands back for a cloud-stored file. It cannot be
     * positioned, so it has to be copied before it can travel resumably.
     */
    private fun nonSeekable(bytes: ByteArray, name: String = "cloud.mp4") = PickedDocument(
        uri = "content://cloud/$name",
        displayName = name,
        length = bytes.size.toLong(),
        lastModifiedMillis = 42L,
        open = { ByteArrayInputStream(bytes) },
        openAt = { throw IOException("this provider only streams") },
    )

    @Test
    fun `a document that can be positioned is used where it is and copied nowhere`() = runTest {
        val document = seekable(payload())

        val staged = stager().stage(document)

        assertNull(staged.cacheFile)
        assertEquals(document.length, staged.source.length)
        assertTrue(cacheRoot.listFiles().isNullOrEmpty())
    }

    @Test
    fun `a document that cannot be positioned is copied into the cache first`() = runTest {
        val bytes = ByteArray(9_000) { (it % 251).toByte() }
        val stager = stager()

        val staged = stager.stage(nonSeekable(bytes))

        val cacheFile = staged.cacheFile
        assertNotNull(cacheFile)
        assertEquals(bytes.size.toLong(), cacheFile!!.length())
        assertArrayEquals(bytes, cacheFile.readBytes())
        // Reading from an offset is possible on the copy, which is the only reason it exists.
        assertEquals(bytes.size - 4_000, staged.source.openAt(4_000L).use { it.readBytes().size })
    }

    @Test
    fun `the copy is announced as its own stage, never as progress`() = runTest {
        var preparing = 0

        stager().stage(nonSeekable(ByteArray(64))) { preparing++ }

        assertEquals(1, preparing)
    }

    @Test
    fun `a document that can be positioned is never announced as preparing`() = runTest {
        var preparing = 0

        stager().stage(seekable(payload())) { preparing++ }

        assertEquals(0, preparing)
    }

    @Test
    fun `a document whose length the provider will not state is copied`() = runTest {
        val bytes = ByteArray(1_024) { 7 }
        val document = PickedDocument(
            uri = "content://docs/unknown",
            displayName = "unknown.bin",
            length = null,
            lastModifiedMillis = null,
            open = { ByteArrayInputStream(bytes) },
            openAt = { throw IOException("no length, no seek") },
        )

        val staged = stager().stage(document)

        assertEquals(bytes.size.toLong(), staged.cacheFile!!.length())
    }

    @Test(expected = InsufficientCacheSpaceException::class)
    fun `a cache that cannot hold the document fails before copying anything`() = runTest {
        free = 100L

        stager().stage(nonSeekable(ByteArray(8_192)))
    }

    @Test
    fun `a refused copy leaves no directory contents behind`() = runTest {
        free = 100L
        val stager = stager()

        runCatching { stager.stage(nonSeekable(ByteArray(8_192))) }

        assertTrue(cacheRoot.listFiles().isNullOrEmpty())
    }

    @Test
    fun `a provider that fails mid-copy leaves no truncated file behind`() = runTest {
        val document = PickedDocument(
            uri = "content://docs/broken",
            displayName = "broken.bin",
            length = 4_096L,
            lastModifiedMillis = 1L,
            open = { streamThatFailsAfter(128) },
            openAt = { throw IOException("no seek") },
        )
        val stager = stager()

        runCatching { stager.stage(document) }

        // A truncated copy under the right name is worse than none: the next attempt would read it.
        assertTrue(cacheRoot.listFiles().isNullOrEmpty())
    }

    @Test
    fun `the cache file is keyed on the source signature, so a changed document gets its own`() {
        val stager = stager()
        val first = seekable(payload().apply { setLastModified(1_000L) })
        val changed = seekable(payload().apply { setLastModified(2_000L) })
        val sameAsFirst = seekable(payload().apply { setLastModified(1_000L) })

        // A document whose modification time moved is not the document the session was opened against.
        assertNotEquals(stager.cacheFileFor(first).name, stager.cacheFileFor(changed).name)
        assertEquals(stager.cacheFileFor(first).name, stager.cacheFileFor(sameAsFirst).name)
    }

    @Test
    fun `discarding a source deletes exactly that source's copy`() = runTest {
        val stager = stager()
        val kept = nonSeekable(ByteArray(32), name = "kept.bin")
        val gone = nonSeekable(ByteArray(48), name = "gone.bin")
        val keptFile = stager.stage(kept).cacheFile!!
        val goneFile = stager.stage(gone).cacheFile!!

        stager.discardCache(gone.sourceKey)

        assertTrue(keptFile.exists())
        assertFalse(goneFile.exists())
    }

    @Test
    fun `trimming deletes every copy no live entry owns and reports how many went`() = runTest {
        val stager = stager()
        val owned = nonSeekable(ByteArray(32), name = "owned.bin")
        val ownedFile = stager.stage(owned).cacheFile!!
        val orphanA = stager.stage(nonSeekable(ByteArray(48), name = "a.bin")).cacheFile!!
        val orphanB = stager.stage(nonSeekable(ByteArray(64), name = "b.bin")).cacheFile!!

        val removed = stager.trim(setOf(owned.sourceKey))

        assertEquals(2, removed)
        assertTrue(ownedFile.exists())
        assertFalse(orphanA.exists())
        assertFalse(orphanB.exists())
    }

    @Test
    fun `trimming a directory that does not exist yet is not an error`() {
        assertEquals(0, stager().trim(emptySet()))
    }

    @Test
    fun `the dispatch threshold is a scheduling choice below the route's own ceiling`() {
        assertTrue(isSingleShotLength(0L))
        assertTrue(isSingleShotLength(UploadProtocol.SINGLE_SHOT_THRESHOLD_BYTES))
        assertFalse(isSingleShotLength(UploadProtocol.SINGLE_SHOT_THRESHOLD_BYTES + 1))
        assertTrue(UploadProtocol.SINGLE_SHOT_THRESHOLD_BYTES < UploadProtocol.SINGLE_SHOT_MAXIMUM_BYTES)
    }

    @Test
    fun `a document that cannot be positioned is recognised before anything is read`() {
        // The probe opens one byte past the start and closes again: it must not consume the document.
        assertFalse(nonSeekable(ByteArray(4)).supportsSeek())
        assertTrue(seekable(payload()).supportsSeek())
    }

    private fun streamThatFailsAfter(count: Int): InputStream =
        object : InputStream() {
            private var remaining = count

            override fun read(): Int {
                if (remaining-- > 0) return 0
                throw IOException("the provider stopped mid-stream")
            }
        }
}
