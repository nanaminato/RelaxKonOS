package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

internal const val CERTIFICATE_ID = "0fded9ef-ed50-4d5e-8e87-74caf3311be7"
internal const val CERTIFICATE_OPERATION_ID = "abcd1234-ed50-4d5e-8e87-74caf3311be7"
internal const val CERTIFICATE_JSON = """{"id":"0fded9ef-ed50-4d5e-8e87-74caf3311be7","primaryDomain":"app.example.test","subjectAlternativeNames":["app.example.test"],"issuer":"Example issuer","serialNumber":"1234","thumbprint":"abcd","notBefore":"2026-09-01T00:00:00Z","notAfter":"2026-12-01T00:00:00Z","status":"issued","challengeType":"webRootHttp01","keyAlgorithm":"ecdsaP256","renewalWindowStart":"2026-11-01T00:00:00Z","renewalWindowEnd":"2026-12-01T00:00:00Z","lastRenewalAt":null,"lastRenewalProblemCode":null,"createdAt":"2026-09-01T00:00:00Z","updatedAt":"2026-09-01T00:00:00Z","kind":"acme","fingerprintSha256":"sha256","renewal":null}"""
internal const val CERTIFICATE_OPERATION_JSON = """{"operationId":"abcd1234-ed50-4d5e-8e87-74caf3311be7","certificateId":"0fded9ef-ed50-4d5e-8e87-74caf3311be7","kind":"issue","state":"running","stage":"running","problemCode":"","startedAt":"2026-09-30T00:00:00Z","completedAt":null}"""
internal const val CERTIFICATE_PREFLIGHT_JSON = """{"canProceed":true,"port80Available":null,"requiresAdministrator":false,"domains":[{"domain":"app.example.test","ipv4Addresses":["192.0.2.1"],"ipv6Addresses":[],"problemCode":""}],"problemCode":"","requiresPublicReachabilityConfirmation":true}"""
internal const val KESTREL_JSON = """{"certificateId":"0fded9ef-ed50-4d5e-8e87-74caf3311be7","certificateExists":true,"httpsConfigured":true,"registered":true,"isDefault":false,"hostNames":["app.example.test"],"fingerprintSha256":"sha256","notBefore":"2026-09-01T00:00:00Z","notAfter":"2026-12-01T00:00:00Z","observedAt":"2026-09-30T00:00:00Z"}"""
class CertificateWireTest {
    @Test fun `renewal history separates automatic failures from successful renewals`() {
        val failed = JSONObject(CERTIFICATE_OPERATION_JSON).put("kind", "renew").put("state", "failed")
            .put("problemCode", "certificate.validation_failed").put("completedAt", "2026-09-30T00:01:00Z")
        val renewal = JSONObject().put("automaticEnabled", true).put("consecutiveFailures", 1)
            .put("retryAfter", "2026-10-01T00:00:00Z").put("retryExhausted", false)
            .put("attempts", org.json.JSONArray().put(JSONObject().put("automatic", true).put("operation", failed)))
        val certificate = CertificateWire.certificate(JSONObject(CERTIFICATE_JSON).put("renewal", renewal).toString())
        assertNull(certificate.lastRenewalAtMillis)
        assertEquals(1, certificate.renewal!!.consecutiveFailures)
        assertNotNull(certificate.renewal.retryAfterMillis)
        val attempt = certificate.renewal.attempts.single()
        assertTrue(attempt.automatic)
        assertEquals(CertificateOperationState.Failed, attempt.operation.state)
        assertNotNull(attempt.operation.completedAtMillis)
        assertEquals("certificate.validation_failed", attempt.operation.problemCode)
        failed.put("kind", "delete")
        assertTrue(runCatching { CertificateWire.certificate(JSONObject(CERTIFICATE_JSON).put("renewal", renewal).toString()) }.isFailure)
    }
    @Test fun `live deployment keeps selector facts and observation time`() {
        val facts = CertificateWire.kestrel(KESTREL_JSON)
        assertTrue(facts.registered); assertFalse(facts.isDefault)
        assertEquals(listOf("app.example.test"), facts.hostNames)
        assertTrue(facts.matches(CertificateWire.certificate(CERTIFICATE_JSON)))
        assertTrue(facts.observedAtMillis > facts.notBeforeMillis!!)
    }
    @Test fun `inconsistent live selector facts are rejected`() {
        assertTrue(runCatching { CertificateWire.kestrel(KESTREL_JSON.replace("\"fingerprintSha256\":\"sha256\"", "\"fingerprintSha256\":null")) }.isFailure)
        assertTrue(runCatching { CertificateWire.kestrel(KESTREL_JSON.replace("\"registered\":true", "\"registered\":false").replace("\"isDefault\":false", "\"isDefault\":true")) }.isFailure)
    }
    @Test fun `current record retains metadata and typed enums`() {
        val certificate = CertificateWire.certificate(CERTIFICATE_JSON)
        assertEquals(CertificateChallenge.WebRootHttp01, certificate.challengeType)
        assertEquals(CertificateKind.Acme, certificate.kind)
        assertEquals("sha256", certificate.fingerprintSha256)
        assertNotNull(certificate.renewalWindowStartMillis)
        assertNull(certificate.lastRenewalAtMillis)
        assertEquals(certificate, CertificateWire.list("[$CERTIFICATE_JSON]").single())
    }
    @Test fun `unknown enums and empty operation IDs are refused`() {
        assertTrue(runCatching { CertificateWire.certificate(CERTIFICATE_JSON.replace("issued", "invented")) }.isFailure)
        assertTrue(runCatching { CertificateWire.operation(CERTIFICATE_OPERATION_JSON.replace(CERTIFICATE_OPERATION_ID, "00000000-0000-0000-0000-000000000000")) }.isFailure)
        assertTrue(runCatching { CertificateWire.operation(CERTIFICATE_OPERATION_JSON.replace("running", "interrupted")) }.isFailure)
    }
    @Test fun `preflight distinguishes unknown host port from available and does not prove reachability`() {
        val preflight = CertificateWire.preflight(CERTIFICATE_PREFLIGHT_JSON)
        assertTrue(preflight.canProceed)
        assertNull(preflight.port80Available)
        assertTrue(preflight.requiresPublicReachabilityConfirmation)
        assertEquals(listOf("192.0.2.1"), preflight.domains.single().ipv4)
    }
    @Test fun `routes and full requests use the current contract`() {
        assertEquals("/api/v1.0/certificates/$CERTIFICATE_ID", CertificateRoutes.mutation(CertificateAction.Delete, CERTIFICATE_ID))
        assertEquals("/api/v1.0/certificates/self-signed", CertificateRoutes.mutation(CertificateAction.SelfSigned, null))
        assertTrue(runCatching { CertificateRoutes.certificate("bad/id") }.isFailure)
        val request = JSONObject(CertificateRequest(listOf("a.test"), CertificateChallenge.WebRootHttp01, "a@example.test", true, CertificateKey.Rsa2048, true).body().toByteArray().decodeToString())
        assertEquals("webRootHttp01", request.getString("challengeType"))
        assertTrue(request.getBoolean("publicReachabilityConfirmed"))
        assertEquals(825, JSONObject(SelfSignedCertificateRequest(listOf("127.0.0.1"), CertificateKey.EcdsaP256, 825).body().toByteArray().decodeToString()).getInt("validityDays"))
    }
}
