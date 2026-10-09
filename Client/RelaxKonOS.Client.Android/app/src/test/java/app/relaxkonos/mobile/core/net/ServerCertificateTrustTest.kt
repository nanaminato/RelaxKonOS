package app.relaxkonos.mobile.core.net

import android.content.SharedPreferences
import java.lang.reflect.Proxy
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.cert.CertificateException
import java.util.Date
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.jce.provider.BouncyCastleProvider
import org.junit.Assert.*
import org.junit.Test

class ServerCertificateTrustTest {
    @Test fun `pins require consent and remain scoped to origin and leaf`() {
        val pins = mutableMapOf<String, String>()
        val editor = Proxy.newProxyInstance(javaClass.classLoader, arrayOf(SharedPreferences.Editor::class.java)) { proxy, method, args ->
            when (method.name) {
                "putString" -> { pins[args!![0] as String] = args[1] as String; proxy }
                "commit" -> true
                else -> proxy
            }
        } as SharedPreferences.Editor
        val preferences = Proxy.newProxyInstance(javaClass.classLoader, arrayOf(SharedPreferences::class.java)) { _, method, args ->
            when (method.name) { "getString" -> pins[args!![0]]; "edit" -> editor; else -> null }
        } as SharedPreferences
        ServerCertificateTrust.initialize(preferences)
        val provider = BouncyCastleProvider()
        val pair = KeyPairGenerator.getInstance("RSA").apply { initialize(2048) }.generateKeyPair()
        fun certificate(serial: Long, until: Long = System.currentTimeMillis() + 86400000) = JcaX509CertificateConverter().setProvider(provider)
            .getCertificate(JcaX509v3CertificateBuilder(X500Name("CN=localhost"), BigInteger.valueOf(serial),
                Date(System.currentTimeMillis() - 86400000), Date(until), X500Name("CN=localhost"), pair.public)
                .build(JcaContentSignerBuilder("SHA256withRSA").setProvider(provider).build(pair.private)))
        val leaf = certificate(1)
        val origin = "https://localhost:5443"
        fun validate(url: String, cert: java.security.cert.X509Certificate = leaf) =
            ServerCertificateTrust.tls(url).second.checkServerTrusted(arrayOf(cert), "RSA")
        fun rejected(url: String, cert: java.security.cert.X509Certificate = leaf) {
            try { validate(url, cert); fail("Certificate must be rejected") } catch (_: CertificateException) { }
        }
        rejected(origin)
        val review = requireNotNull(ServerCertificateTrust.review(origin))
        assertEquals(64, review.fingerprint.length)
        ServerCertificateTrust.trust(review)
        validate(origin)
        validate("https://LOCALHOST:5443")
        ServerCertificateTrust.initialize(preferences)
        validate(origin)
        rejected("https://localhost:5444")
        rejected("https://other-host:5443")
        rejected(origin, certificate(2))
        assertEquals(review.fingerprint, ServerCertificateTrust.review(origin)?.previous)
        ServerCertificateTrust.review(origin)?.let(ServerCertificateTrust::clear)
        rejected(origin, certificate(3, System.currentTimeMillis() - 3600000))
        assertNull(ServerCertificateTrust.review(origin))
        val tunnelA = "https://127.0.0.1:51000"
        val tunnelB = "https://127.0.0.1:52000"
        val bindingA = ServerCertificateTrust.bindTunnel(tunnelA, "ssh-tunnel:profile-a")
        rejected(tunnelA)
        ServerCertificateTrust.trust(requireNotNull(ServerCertificateTrust.review(tunnelA)))
        ServerCertificateTrust.unbindTunnel(bindingA)
        val bindingB = ServerCertificateTrust.bindTunnel(tunnelB, "ssh-tunnel:profile-a")
        validate(tunnelB)
        rejected(tunnelA)
        val oldManager = ServerCertificateTrust.tls(tunnelB).second
        ServerCertificateTrust.unbindTunnel(bindingB)
        val replacement = ServerCertificateTrust.bindTunnel(tunnelB, "ssh-tunnel:profile-b")
        rejected(tunnelB)
        val replacementReview = requireNotNull(ServerCertificateTrust.review(tunnelB))
        ServerCertificateTrust.unbindTunnel(bindingB)
        assertSame(replacementReview, ServerCertificateTrust.review(tunnelB))
        try { oldManager.checkServerTrusted(arrayOf(leaf), "RSA"); fail("Old manager must not accept a replaced binding") }
        catch (_: CertificateException) { }
        assertSame(replacementReview, ServerCertificateTrust.review(tunnelB))
        val renewedBinding = ServerCertificateTrust.bindTunnel(tunnelB, "ssh-tunnel:profile-b")
        rejected(tunnelB)
        val repeatedReview = requireNotNull(ServerCertificateTrust.review(tunnelB))
        rejected(tunnelB)
        val renewedReview = requireNotNull(ServerCertificateTrust.review(tunnelB))
        assertEquals(repeatedReview, renewedReview)
        assertNotSame(repeatedReview, renewedReview)
        ServerCertificateTrust.clear(repeatedReview)
        ServerCertificateTrust.clear(replacementReview)
        ServerCertificateTrust.clear(renewedReview.copy())
        assertSame(renewedReview, ServerCertificateTrust.review(tunnelB))
        assertThrows(IllegalStateException::class.java) { ServerCertificateTrust.trust(replacementReview) }
        ServerCertificateTrust.trust(renewedReview)
        val sameScopeOld = ServerCertificateTrust.tls(tunnelB).second
        val sameScopeReplacement = ServerCertificateTrust.bindTunnel(tunnelB, "ssh-tunnel:profile-b")
        ServerCertificateTrust.unbindTunnel(replacement)
        ServerCertificateTrust.unbindTunnel(renewedBinding)
        validate(tunnelB)
        try { sameScopeOld.checkServerTrusted(arrayOf(leaf), "RSA"); fail("A same-scope replacement still invalidates the old lease") }
        catch (_: CertificateException) { }
        ServerCertificateTrust.unbindTunnel(sameScopeReplacement)
        rejected(origin, certificate(2))
        val updatedPinReview = requireNotNull(ServerCertificateTrust.review(origin))
        val checkingOldLeaf = VerificationCallbackCertificate(leaf) { ServerCertificateTrust.trust(updatedPinReview) }
        rejected(origin, checkingOldLeaf)
        assertEquals(updatedPinReview.fingerprint, ServerCertificateTrust.review(origin)?.previous)
    }
}

/** Keeps real DER and signature verification, while simulating consent during a handshake. */
private class VerificationCallbackCertificate(private val leaf: java.security.cert.X509Certificate,
    private val callback: () -> Unit) : java.security.cert.X509Certificate() {
    private var called = false
    private fun verified() { if (!called) { called = true; callback() } }
    override fun verify(key: java.security.PublicKey) { verified(); leaf.verify(key) }
    override fun verify(key: java.security.PublicKey, provider: String) { verified(); leaf.verify(key, provider) }
    override fun checkValidity() = leaf.checkValidity()
    override fun checkValidity(date: Date) = leaf.checkValidity(date)
    override fun getVersion() = leaf.version
    override fun getSerialNumber() = leaf.serialNumber
    override fun getIssuerDN() = leaf.issuerX500Principal
    override fun getSubjectDN() = leaf.subjectX500Principal
    override fun getNotBefore() = leaf.notBefore
    override fun getNotAfter() = leaf.notAfter
    override fun getTBSCertificate() = leaf.tbsCertificate
    override fun getSignature() = leaf.signature
    override fun getSigAlgName() = leaf.sigAlgName
    override fun getSigAlgOID() = leaf.sigAlgOID
    override fun getSigAlgParams() = leaf.sigAlgParams
    override fun getIssuerUniqueID() = leaf.issuerUniqueID
    override fun getSubjectUniqueID() = leaf.subjectUniqueID
    override fun getKeyUsage() = leaf.keyUsage
    override fun getBasicConstraints() = leaf.basicConstraints
    override fun getEncoded() = leaf.encoded
    override fun getPublicKey() = leaf.publicKey
    override fun hasUnsupportedCriticalExtension() = leaf.hasUnsupportedCriticalExtension()
    override fun getCriticalExtensionOIDs() = leaf.criticalExtensionOIDs
    override fun getNonCriticalExtensionOIDs() = leaf.nonCriticalExtensionOIDs
    override fun getExtensionValue(oid: String) = leaf.getExtensionValue(oid)
    override fun toString() = leaf.toString()
}
