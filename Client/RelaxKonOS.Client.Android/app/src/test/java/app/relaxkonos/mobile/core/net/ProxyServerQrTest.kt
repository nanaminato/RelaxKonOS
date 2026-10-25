package app.relaxkonos.mobile.core.net

import com.google.zxing.BinaryBitmap
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import org.junit.Assert.*
import org.junit.Test

class ProxyServerQrTest {
    @Test fun `connection URI QR decodes to the exact credential without network or storage`() {
        val content = "vless://00112233-4455-6677-8899-aabbccddeeff@proxy.example.test:443?security=reality&type=tcp#phone"
        val matrix = requireNotNull(ProxyServerQr.encode(ProxyServerExportFormats.URI, content))
        val pixels = IntArray(matrix.width * matrix.height) { if (matrix[it % matrix.width, it / matrix.width]) 0xff000000.toInt() else 0xffffffff.toInt() }
        val bitmap = BinaryBitmap(HybridBinarizer(RGBLuminanceSource(matrix.width, matrix.height, pixels)))
        assertEquals(content, QRCodeReader().decode(bitmap).text)
    }
    @Test fun `only bounded single line URI exports receive QR codes`() {
        assertNull(ProxyServerQr.encode(ProxyServerExportFormats.SING_BOX, "{}"))
        assertNull(ProxyServerQr.encode(ProxyServerExportFormats.URI, "a\nb"))
        assertNull(ProxyServerQr.encode(ProxyServerExportFormats.URI, "a".repeat(2000)))
        assertNull(ProxyServerQr.encode(ProxyServerExportFormats.URI, ""))
    }
}
