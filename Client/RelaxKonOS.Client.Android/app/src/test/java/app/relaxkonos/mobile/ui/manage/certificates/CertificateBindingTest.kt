package app.relaxkonos.mobile.ui.manage.certificates

import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class CertificateBindingTest {
    private val certificate = CertificateWire.certificate(CERTIFICATE_JSON)
    @Test fun `missing selection never falls through to implicit certificate issuance`() {
        assertEquals(CertificateSelectionProblem.Required, certificateSelectionProblem(ApiResult.Success(listOf(certificate)), null, listOf("app.example.test")))
    }
    @Test fun `unverified list differs from missing saved ID`() {
        assertEquals(CertificateSelectionProblem.Unverified, certificateSelectionProblem(ApiResult.Transport(null), CERTIFICATE_ID, emptyList()))
        assertEquals(CertificateSelectionProblem.Missing, certificateSelectionProblem(ApiResult.Success(emptyList()), CERTIFICATE_ID, emptyList()))
    }
    @Test fun `domain changes invalidate the explicitly selected certificate`() {
        val list = ApiResult.Success(listOf(certificate)); val now = certificate.notBeforeMillis!!
        assertNull(certificateSelectionProblem(list, CERTIFICATE_ID, listOf("app.example.test"), now))
        assertEquals(CertificateSelectionProblem.Ineligible, certificateSelectionProblem(list, CERTIFICATE_ID, listOf("other.example.test"), now))
    }
}
