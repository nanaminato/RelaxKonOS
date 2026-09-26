package app.relaxkonos.mobile.data

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.media.ExifInterface
import app.relaxkonos.mobile.core.net.RemoteEntry
import java.io.File

/**
 * Turns one cached file into something the screen can draw.
 *
 * It is an interface for one reason: the platform decoder cannot run in a JVM test — `BitmapFactory`
 * is a stub there — so everything that decides *how much* of an image to decode lives in
 * [previewSampleSize], which is a pure function with its own tests, and this type is left with nothing
 * but the call itself.
 */
interface ImageDecoder {
    /**
     * Decodes [file] so that the result is no larger than `maxWidthPx` × `maxHeightPx`, or `null` when
     * it cannot be shown at all.
     *
     * A `null` is a normal answer, not an error: the file may not be an image, may use a codec this
     * platform build does not carry, or may be a scan too large for the heap.
     */
    fun decode(file: File, maxWidthPx: Int, maxHeightPx: Int): Bitmap?

    /**
     * Decodes image **bytes** that were already rendered for a screen, so there is nothing to measure
     * before decoding them.
     *
     * This exists for the server's thumbnail: it arrives at the size that was asked of the server, and
     * `BitmapFactory` cannot subsample a byte array it has not measured in the same pass anyway.
     *
     * No orientation step is needed here, unlike [decode]: the server applies the EXIF rotation before
     * it encodes, so what arrives is already the right way up.
     *
     * `null` means the same as it does above — these bytes are not a picture this device can draw.
     */
    fun decode(bytes: ByteArray): Bitmap?
}

/**
 * The platform's own decoders, driven through `BitmapFactory`.
 *
 * Two passes over the file, which is the point of the whole exercise: `inJustDecodeBounds` reads only
 * the header to learn the image's real size, and the second pass decodes it subsampled to that size.
 * Decoding first and scaling afterwards would allocate the full image, and a modern phone camera photo
 * is 40 megapixels — 160 MB in `ARGB_8888` — which is more than the app's heap.
 */
class BitmapFactoryImageDecoder : ImageDecoder {
    override fun decode(file: File, maxWidthPx: Int, maxHeightPx: Int): Bitmap? {
        // Nothing here may take the process down. A `decodeFile` on a file that is not an image, or on
        // one whose codec this platform build lacks, answers `null`; a scan bigger than the heap
        // answers `OutOfMemoryError`. Both are "this file cannot be shown", which the caller already
        // has a message for, so even the error is caught rather than allowed to reach the handler.
        return runCatching {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeFile(file.absolutePath, bounds)
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) {
                return@runCatching null
            }
            val options = BitmapFactory.Options().apply {
                inSampleSize = previewSampleSize(bounds.outWidth, bounds.outHeight, maxWidthPx, maxHeightPx)
            }
            val decoded = BitmapFactory.decodeFile(file.absolutePath, options) ?: return@runCatching null
            decoded.orientedBy(file.exifOrientation())
        }.getOrNull()
    }

    /**
     * One pass over the bytes, with no bounds probe and no subsample.
     *
     * That is not a shortcut: the bytes are a thumbnail the server rendered at the size it was asked
     * for, so the only correct number of passes is one. A caller that has a bigger picture than it
     * wants to draw should write it to a file and use [decode] instead.
     */
    override fun decode(bytes: ByteArray): Bitmap? = runCatching {
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
    }.getOrNull()
}

/**
 * How much smaller than the original the decoded bitmap has to be, as a power of two.
 *
 * Two rules, in this order:
 *
 *  1. **Never decode less detail than the box can show.** The loop stops as soon as halving again would
 *     drop the image below the display size, so a thumbnail-sized request still decodes the whole
 *     picture and an image already smaller than the box is decoded as it is.
 *  2. **Never spend more than [maxPixels] on one image.** Rule 1 alone is not a memory bound: it picks
 *     powers of two, so the result can be up to four times the box in pixels, and a phone showing a
 *     220 dp card has no business holding a 4000×3000 bitmap. This is the rule that keeps a
 *     full-screen viewer on a tablet from asking for more than the heap can hold.
 *
 * `BitmapFactory` accepts only powers of two for `inSampleSize` without extra scaling work, so the
 * result is deliberately coarser than exact — it is never smaller than the box, which is the direction
 * that matters for what the user sees.
 */
internal fun previewSampleSize(
    width: Int,
    height: Int,
    maxWidthPx: Int,
    maxHeightPx: Int,
    maxPixels: Int = MAX_PREVIEW_PIXELS,
): Int {
    // A box of zero would make the first loop below run forever: `width / (sample * 2) >= 0` is true
    // for every sample, and `sample` would double past `Int.MAX_VALUE` and back to zero.
    if (width <= 0 || height <= 0 || maxWidthPx <= 0 || maxHeightPx <= 0 || maxPixels <= 0) {
        return 1
    }
    var sample = 1
    while (width / (sample * 2) >= maxWidthPx && height / (sample * 2) >= maxHeightPx) {
        sample *= 2
    }
    // In `Long`: a 50000 × 50000 panorama is 2.5 billion pixels, which is more than an `Int` holds, and
    // the comparison has to be able to say so — a wrapped negative would read as "small enough".
    while (width.toLong() / sample * (height.toLong() / sample) > maxPixels) {
        sample *= 2
    }
    return sample
}

/**
 * Whether this entry is worth trying to show.
 *
 * The name is what decides, because it is what the user sees and what the file actually is; the type
 * the server inferred only gets a vote when the name says nothing — a file called `IMG_0042` with no
 * extension is an image if the host says it is one. The two never disagree in a way that matters: a name
 * whose extension the platform can decode wins, and a type the platform cannot decode loses even when
 * the server calls it an image, which is how an SVG ends up with the file icon instead of a blank card.
 */
fun RemoteEntry.isDecodableImage(): Boolean = !isDirectory && isDecodableImage(name, mimeType)

internal fun isDecodableImage(name: String, mimeType: String?): Boolean {
    if (name.substringAfterLast('.', "").lowercase() in DECODABLE_IMAGE_EXTENSIONS) {
        return true
    }
    val type = mimeType?.substringBefore(';')?.trim()?.lowercase() ?: return false
    return type.startsWith("image/") && type !in UNDECODABLE_IMAGE_TYPES
}

/** Extensions `BitmapFactory` knows on every supported API level, plus the ones it knows from 28. */
private val DECODABLE_IMAGE_EXTENSIONS = setOf(
    "jpg", "jpeg", "jpe", "jfif", "png", "apng", "webp", "gif", "bmp", "heic", "heif", "avif",
)

/**
 * Image types the platform decodes no better than an SVG does.
 *
 * Listing these rather than trusting the `image/` prefix keeps the preview card honest: a TIFF or an
 * icon handed to `BitmapFactory` produces no bitmap, and the user would read "this file cannot be shown"
 * for a file the app itself had just claimed to be previewable.
 */
private val UNDECODABLE_IMAGE_TYPES = setOf(
    "image/svg+xml", "image/tiff", "image/x-icon", "image/vnd.microsoft.icon",
)

/**
 * An `ARGB_8888` bitmap of this many pixels is 16 MB, which is a reasonable ceiling for one preview on
 * a phone and still far more than any box the app draws it in.
 */
private const val MAX_PREVIEW_PIXELS = 4_000_000

/** The EXIF orientation of a camera photo, or [ExifInterface.ORIENTATION_NORMAL] when there is none. */
private fun File.exifOrientation(): Int = runCatching {
    ExifInterface(absolutePath).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)
}.getOrDefault(ExifInterface.ORIENTATION_NORMAL)

/**
 * Rotates and mirrors a freshly decoded bitmap according to its EXIF orientation.
 *
 * `BitmapFactory` reads pixels and nothing else, so a photo taken in portrait on a phone comes back on
 * its side; this is the step that stands it up. The source is recycled on success — it was decoded a
 * moment ago, nothing else holds it, and keeping both copies alive doubles the peak of exactly the
 * decode that is most likely to be large.
 */
private fun Bitmap.orientedBy(orientation: Int): Bitmap {
    val matrix = orientationMatrix(orientation) ?: return this
    val rotated = Bitmap.createBitmap(this, 0, 0, width, height, matrix, true)
    if (rotated !== this) {
        recycle()
    }
    return rotated
}

/** The transform each EXIF orientation asks for; `null` for the common case of no transform at all. */
private fun orientationMatrix(orientation: Int): Matrix? = when (orientation) {
    ExifInterface.ORIENTATION_ROTATE_90 -> Matrix().apply { setRotate(90f) }
    ExifInterface.ORIENTATION_ROTATE_180 -> Matrix().apply { setRotate(180f) }
    ExifInterface.ORIENTATION_ROTATE_270 -> Matrix().apply { setRotate(270f) }
    ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> Matrix().apply { setScale(-1f, 1f) }
    ExifInterface.ORIENTATION_FLIP_VERTICAL -> Matrix().apply { setScale(1f, -1f) }
    ExifInterface.ORIENTATION_TRANSPOSE -> Matrix().apply {
        setRotate(90f)
        postScale(-1f, 1f)
    }
    ExifInterface.ORIENTATION_TRANSVERSE -> Matrix().apply {
        setRotate(-90f)
        postScale(-1f, 1f)
    }
    else -> null
}
