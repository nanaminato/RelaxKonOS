package app.relaxkonos.mobile.core.net

import org.junit.Assert.*
import org.junit.Test

class CertificateUsageTest {
    private val certificate = CertificateWire.certificate(CERTIFICATE_JSON)
    @Test fun `wildcards match one label and never the apex or IP`() {
        assertTrue(CertificateNames.covers(listOf("*.example.test"), "ONE.EXAMPLE.TEST."))
        assertFalse(CertificateNames.covers(listOf("*.example.test"), "deep.one.example.test"))
        assertFalse(CertificateNames.covers(listOf("*.example.test"), "example.test"))
        assertFalse(CertificateNames.covers(listOf("*.0.0.1"), "127.0.0.1"))
        assertTrue(CertificateNames.covers(listOf("*.example.test"), "*.example.test"))
    }
    @Test fun `IDNs and literal IPs use normalized exact coverage`() {
        assertTrue(CertificateNames.covers(listOf("xn--bcher-kva.example"), "bücher.example"))
        assertTrue(CertificateNames.covers(listOf("2001:db8::1"), "2001:0db8:0:0:0:0:0:1"))
        assertFalse(CertificateNames.covers(listOf("app.example.test"), "other.example.test"))
        assertNull(CertificateNames.normalize("bad name.test"))
    }
    @Test fun `validity boundaries status and all SAN bindings are enforced`() {
        val start = certificate.notBeforeMillis!!; val end = certificate.notAfterMillis!!
        assertNull(certificate.usageProblem(listOf("app.example.test"), start))
        assertEquals(CertificateUsageProblem.NotYetValid, certificate.usageProblem(nowMillis = start - 1))
        assertEquals(CertificateUsageProblem.Expired, certificate.usageProblem(nowMillis = end))
        assertEquals(CertificateUsageProblem.DomainMismatch, certificate.usageProblem(listOf("app.example.test", "other.example.test"), start))
        assertEquals(CertificateUsageProblem.UnusableStatus, certificate.copy(status = CertificateStatus.Revoked).usageProblem(nowMillis = start))
        assertEquals(CertificateUsageProblem.MetadataUnavailable, certificate.copy(notAfterMillis = null).usageProblem(nowMillis = start))
    }
    @Test fun `runtime fingerprint compares formats and certificate identity`() {
        val facts = KestrelCertificateDeployment(CERTIFICATE_ID, true, true, true, false, listOf("app.example.test"), "AA:BB", 1, 2, 3)
        assertTrue(facts.matches(certificate.copy(fingerprintSha256 = "aabb")))
        assertFalse(facts.copy(registered = false).matches(certificate.copy(fingerprintSha256 = "aabb")))
        assertFalse(facts.copy(certificateExists = false).matches(certificate.copy(fingerprintSha256 = "aabb")))
        assertFalse(facts.copy(fingerprintSha256 = "different").matches(certificate))
    }
}
