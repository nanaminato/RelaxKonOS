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
        ServerCertificateTrust.initialize(preferences)
        validate(origin)
        rejected("https://localhost:5444")
        rejected("https://other-host:5443")
        rejected(origin, certificate(2))
        assertEquals(review.fingerprint, ServerCertificateTrust.review(origin)?.previous)
        ServerCertificateTrust.clear(origin)
        rejected(origin, certificate(3, System.currentTimeMillis() - 3600000))
        assertNull(ServerCertificateTrust.review(origin))
        val tunnelA = "https://127.0.0.1:51000"
        val tunnelB = "https://127.0.0.1:52000"
        ServerCertificateTrust.bindTunnel(tunnelA, "ssh-tunnel:profile-a")
        rejected(tunnelA)
        ServerCertificateTrust.trust(requireNotNull(ServerCertificateTrust.review(tunnelA)))
        ServerCertificateTrust.unbindTunnel(tunnelA)
        ServerCertificateTrust.bindTunnel(tunnelB, "ssh-tunnel:profile-a")
        validate(tunnelB)
        rejected(tunnelA)
        ServerCertificateTrust.unbindTunnel(tunnelB)
        ServerCertificateTrust.bindTunnel(tunnelB, "ssh-tunnel:profile-b")
        rejected(tunnelB)
        ServerCertificateTrust.unbindTunnel(tunnelB)
    }
}
