package app.relaxkonos.mobile.ui.manage.certificates

import app.relaxkonos.mobile.core.net.ApiResult
import org.junit.Assert.*
import org.junit.Test

class CertificateFailureTest {
    @Test fun `read transport failure asks for refresh without inventing an unknown write`() {
        val feedback = certificateFailure(ApiResult.Transport("private detail"), false)
        assertEquals("certificate.read_failed", feedback.problemCode)
        assertFalse(feedback.uncertain)
    }
    @Test fun `write transport failure requires verification rather than a read failure`() {
        val feedback = certificateFailure(ApiResult.Transport("private detail"), true)
        assertNull(feedback.problemCode)
        assertTrue(feedback.uncertain)
    }
    @Test fun `authoritative rejection and success keep their existing meaning in either phase`() {
        for (writing in listOf(false, true)) {
            val rejected = certificateFailure(ApiResult.Problem(403, "certificate.admin_required", null), writing)
            assertEquals("certificate.admin_required", rejected.problemCode); assertFalse(rejected.uncertain)
            val success = certificateFailure(ApiResult.Success(Unit), writing)
            assertNull(success.problemCode); assertFalse(success.uncertain)
        }
    }
}
