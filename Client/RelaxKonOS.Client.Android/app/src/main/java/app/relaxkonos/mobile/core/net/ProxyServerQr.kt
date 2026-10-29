package app.relaxkonos.mobile.core.net

import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.common.BitMatrix
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel

/** Local-only disclosure. A QR code has the same lifetime and sensitivity as its exported URI. */
internal object ProxyServerQr {
    fun encode(format: String, content: String): BitMatrix? {
        if (format != ProxyServerExportFormats.URI || content.isEmpty() || content.length >= 2000 || content.any { it == '\n' || it == '\r' }) return null
        return runCatching { QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, 512, 512,
            mapOf(EncodeHintType.CHARACTER_SET to "UTF-8", EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.Q, EncodeHintType.MARGIN to 4)) }.getOrNull()
    }
}
