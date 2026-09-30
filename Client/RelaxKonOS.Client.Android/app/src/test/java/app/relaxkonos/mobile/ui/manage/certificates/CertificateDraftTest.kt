package app.relaxkonos.mobile.ui.manage.certificates

import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class CertificateDraftTest {
    @Test fun `ACME normalizes SANs and requires terms email and reachability`() {
        val draft = CertificateDraft(domainsText = "B.example.test, a.example.test B.example.test", email = "a@example.test", acceptedTerms = true, reachable = true)
        assertEquals(listOf("a.example.test", "b.example.test"), draft.domains())
        assertNotNull(draft.body())
        assertNull(draft.copy(acceptedTerms = false).body()); assertNull(draft.copy(reachable = false).body()); assertNull(draft.copy(email = "bad").body())
    }
    @Test fun `DNS mode has no fake submission or DNS token field`() {
        val draft = CertificateDraft(domainsText = "*.example.test", email = "a@example.test", challenge = CertificateChallenge.Dns01, acceptedTerms = true, reachable = true)
        assertNotNull(draft.domains()); assertNull(draft.body())
        assertNull(draft.copy(challenge = CertificateChallenge.DirectHttp01).domains())
    }
    @Test fun `self signed supports private IP and DNS identities without ACME confirmation`() {
        val draft = CertificateDraft(selfSigned = true, domainsText = "127.0.0.1 localhost ::1 *.example.test")
        assertNotNull(draft.body()); assertEquals(CertificateAction.SelfSigned, draft.action)
        assertNull(draft.copy(selfSigned = false).domains())
    }
    @Test fun `validity bounds and invalid names block creation`() {
        val draft = CertificateDraft(selfSigned = true, domainsText = "example.test")
        assertNull(draft.copy(validityDays = "0").body()); assertNull(draft.copy(validityDays = "826").body()); assertNotNull(draft.copy(validityDays = "825").body())
        assertNull(draft.copy(domainsText = "https://example.test").body()); assertNull(draft.copy(domainsText = "").body())
    }
    @Test fun `IDN names are sent in canonical ASCII`() {
        assertEquals(listOf("xn--r8jz45g.xn--zckzah"), CertificateDraft(domainsText = "例え.テスト").domains())
    }
}
