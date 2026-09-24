package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.RemoteEntry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * What the preview decides before any bytes move: how much of an image is worth decoding, and which
 * entries are worth decoding at all.
 *
 * Both are pure functions, which is the point of them existing separately from `BitmapFactoryImageDecoder`:
 * `BitmapFactory` is a stub in a JVM test, so everything with a decision in it lives here where it can
 * actually be pinned down.
 */
class ImageDecoderTest {
    // ---- How much to decode --------------------------------------------

    @Test
    fun `an image smaller than the box is decoded whole`() {
        assertEquals(1, previewSampleSize(640, 480, 1080, 1080))
    }

    @Test
    fun `a photo is halved once for a phone-sized preview`() {
        assertEquals(2, previewSampleSize(4000, 3000, 1080, 1080))
    }

    @Test
    fun `a forty megapixel photo is quartered`() {
        assertEquals(4, previewSampleSize(8000, 6000, 1080, 1080))
    }

    @Test
    fun `the result never drops below the box it is drawn in`() {
        // Sampling is by powers of two, so the honest bound is "not smaller than the box", not "equal
        // to it": a 4000-pixel image in a 1080 box decodes at 2000, which is the smallest power of two
        // that still covers it.
        val sample = previewSampleSize(4000, 3000, 1080, 1080)
        assertTrue(4000 / sample >= 1080)
        assertTrue(3000 / sample >= 1080)
        // One step further would have gone below the box, which is what makes this the right answer.
        assertTrue(4000 / (sample * 2) < 1080)
    }

    @Test
    fun `a panorama is cut down to the pixel budget`() {
        val sample = previewSampleSize(20000, 2000, 1080, 1080)
        assertTrue(20000L / sample * (2000L / sample) <= 4_000_000L)
    }

    @Test
    fun `a scan larger than an int can count is still cut down`() {
        // 50000 × 50000 is 2.5 billion pixels: an `Int` product wraps to a negative number, which would
        // read as "well inside the budget" and try to allocate 10 GB.
        val sample = previewSampleSize(50000, 50000, 1080, 1080)
        assertTrue(sample > 1)
        assertTrue(50000L / sample * (50000L / sample) <= 4_000_000L)
    }

    @Test
    fun `a box of nothing decodes at the original size`() {
        // A box of zero would make "half of this is still bigger than the box" true forever.
        assertEquals(1, previewSampleSize(4000, 3000, 0, 0))
    }

    @Test
    fun `an image of nothing decodes at the original size`() {
        assertEquals(1, previewSampleSize(0, 0, 1080, 1080))
    }

    // ---- What is worth decoding ----------------------------------------

    @Test
    fun `a name the platform can decode is enough`() {
        assertTrue(isDecodableImage("photo.png", null))
        assertTrue(isDecodableImage("PHOTO.JPG", null))
        assertTrue(isDecodableImage("clip.webp", "application/octet-stream"))
    }

    @Test
    fun `a name with no extension falls back to what the host called it`() {
        assertTrue(isDecodableImage("IMG_0042", "image/jpeg"))
        assertFalse(isDecodableImage("IMG_0042", "application/octet-stream"))
        assertFalse(isDecodableImage("IMG_0042", null))
    }

    @Test
    fun `an image type the platform cannot decode is not offered`() {
        // The card would otherwise promise a picture and then report that the file cannot be shown.
        assertFalse(isDecodableImage("vector.svg", "image/svg+xml"))
        assertFalse(isDecodableImage("scan.tiff", "image/tiff"))
    }

    @Test
    fun `something that is not an image is not offered`() {
        assertFalse(isDecodableImage("notes.txt", "text/plain"))
        assertFalse(isDecodableImage("archive.zip", "application/zip"))
    }

    @Test
    fun `a folder is never an image, whatever it is called`() {
        val folder = RemoteEntry(
            path = "/pics/album.png",
            name = "album.png",
            isDirectory = true,
            sizeBytes = null,
            modifiedAtMillis = null,
            mimeType = "inode/directory",
        )
        assertFalse(folder.isDecodableImage())
    }

    @Test
    fun `a file entry is an image when its name says so`() {
        val file = RemoteEntry(
            path = "/pics/album.png",
            name = "album.png",
            isDirectory = false,
            sizeBytes = 1024,
            modifiedAtMillis = null,
            mimeType = "image/png",
        )
        assertTrue(file.isDecodableImage())
    }
}
