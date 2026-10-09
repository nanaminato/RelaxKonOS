package app.relaxkonos.mobile.core.net

import android.content.SharedPreferences
import java.lang.reflect.Proxy
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Date
import kotlinx.coroutines.test.runTest
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.jce.provider.BouncyCastleProvider
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Owner-device pairing reported "cannot reach the server" against a self-signed host and could never
 * be completed: nothing probed the invitation's origin, so no review existed and no consent was ever
 * asked for. These tests drive the real trust manager and a real self-signed leaf, so they fail again
 * if a flow stops asking before it sends.
 *
 * Only the pin store is faked, and through a proxy rather than the platform: the assertion that
 * matters is that a certificate is pinned exactly once, and that the next attempt sends its request
 * without a second question.
 */
class ServerCertificateConfirmationTest {
    @Test fun `actual http discovery preserves another flows pending https review`() = runTest {
        ServerCertificateTrust.initialize(emptyPreferences())
        val server = java.net.ServerSocket(0, 1, java.net.InetAddress.getByName("127.0.0.1"))
        server.soTimeout = 8_000
        val https = "https://127.0.0.1:${server.localPort}"
        val http = "http://127.0.0.1:${server.localPort}"
        runCatching { handshake(https, selfSigned(12)) }
        val review = requireNotNull(ServerCertificateTrust.review(https))
        val failure = java.util.concurrent.atomic.AtomicReference<Throwable?>()
        val serving = kotlin.concurrent.thread(isDaemon = true, name = "http-discovery-scope-test") {
            try {
                server.accept().use { socket ->
                    socket.soTimeout = 4_000
                    check(socket.getInputStream().read() == 'O'.code)
                    socket.getOutputStream().write("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray())
                    socket.getOutputStream().flush()
                }
            } catch (error: Throwable) { if (!server.isClosed) failure.set(error) }
        }
        try {
            assertEquals(EndpointDiscoveryResult.Found(http), ServerEndpointDiscovery.discover(http))
            serving.join(2_000)
            assertFalse(serving.isAlive)
            assertNull(failure.get())
            assertTrue(ServerCertificateTrust.review(https) === review)
        } finally { server.close(); serving.join(5_000) }
    }
    @Test fun `explicit http ignores but does not erase a pending https observation`() = runTest {
        ServerCertificateTrust.initialize(emptyPreferences())
        val https = "https://192.168.1.7:5501"
        runCatching { handshake(https, selfSigned(11)) }
        val review = requireNotNull(ServerCertificateTrust.review(https))
        assertNull(ServerCertificateConfirmation.pending(listOf("http://192.168.1.7:5501")))
        var probes = 0
        assertNull(ServerCertificateConfirmation.reviewFor(listOf("http://192.168.1.7:5501")) { probes++ })
        assertEquals(1, probes)
        assertTrue(ServerCertificateTrust.review(https) === review)
        assertTrue(ServerCertificateConfirmation.pending(listOf(https)) === review)
        assertTrue(ServerCertificateConfirmation.pending(listOf("HTTPS://192.168.1.7:5501")) === review)
        assertNull(ServerCertificateConfirmation.pending(listOf("HTTP://192.168.1.7:5501")))
    }

    @Test
    fun `a self-signed pairing origin is pinned once and never asked again`() = runTest {
        ServerCertificateTrust.initialize(emptyPreferences())
        val leaf = selfSigned(1)
        val origin = "https://192.168.1.7:5000"
        var handshakes = 0
        val probe: suspend () -> Unit = {
            handshakes++
            // What an HTTP stack does with this origin: the trust manager the app installs on it.
            runCatching { handshake(origin, leaf) }
            Unit
        }

        val review = requireNotNull(ServerCertificateConfirmation.reviewFor(listOf(origin), probe))
        assertEquals(1, handshakes)
        assertEquals(origin, review.origin)
        assertEquals(64, review.fingerprint.length)
        // The failed handshake recorded the review; it trusted nothing.
        assertThrows(CertificateException::class.java) { handshake(origin, leaf) }

        assertTrue(ServerCertificateConfirmation.consent(review))
        handshake(origin, leaf)

        // The pin is written, so the next attempt probes, finds nothing to ask, and sends its request.
        assertNull(ServerCertificateConfirmation.reviewFor(listOf(origin), probe))
        assertEquals(2, handshakes)
    }

    @Test
    fun `consent answers one observation and cannot be replayed`() = runTest {
        ServerCertificateTrust.initialize(emptyPreferences())
        val leaf = selfSigned(2)
        val origin = "https://10.0.0.5:5000"
        val review = requireNotNull(
            ServerCertificateConfirmation.reviewFor(listOf(origin)) { runCatching { handshake(origin, leaf) } },
        )

        assertTrue(ServerCertificateConfirmation.consent(review))
        assertFalse(ServerCertificateConfirmation.consent(review))
    }

    @Test
    fun `a review left by an earlier probe is answered without probing again`() = runTest {
        ServerCertificateTrust.initialize(emptyPreferences())
        val leaf = selfSigned(3)
        val origin = "https://10.0.0.6:5000"
        // The address probe the login screen already ran, which is what left the review behind.
        runCatching { handshake(origin, leaf) }

        var probes = 0
        val review = requireNotNull(ServerCertificateConfirmation.reviewFor(listOf(origin)) { probes++ })
        assertEquals(0, probes)
        assertEquals(origin, review.origin)
        // Declining changes nothing about the question: asking again reports the same review and still
        // sends nothing, and no probe is repeated to get there.
        assertEquals(review, ServerCertificateConfirmation.reviewFor(listOf(origin)) { probes++ })
        assertEquals(0, probes)
    }

    @Test
    fun `an address that never presented a certificate has nothing to ask`() = runTest {
        ServerCertificateTrust.initialize(emptyPreferences())
        var probes = 0
        assertNull(ServerCertificateConfirmation.reviewFor(listOf("http://10.0.2.2:5090")) { probes++ })
        assertEquals(1, probes)
    }

    private fun handshake(origin: String, leaf: X509Certificate) =
        ServerCertificateTrust.tls(origin).second.checkServerTrusted(arrayOf(leaf), "RSA")

    private fun selfSigned(serial: Long): X509Certificate {
        val provider = BouncyCastleProvider()
        val pair = KeyPairGenerator.getInstance("RSA").apply { initialize(2048) }.generateKeyPair()
        val name = X500Name("CN=localhost")
        val builder = JcaX509v3CertificateBuilder(
            name,
            BigInteger.valueOf(serial),
            Date(System.currentTimeMillis() - 86_400_000),
            Date(System.currentTimeMillis() + 86_400_000),
            name,
            pair.public,
        )
        return JcaX509CertificateConverter().setProvider(provider)
            .getCertificate(builder.build(JcaContentSignerBuilder("SHA256withRSA").setProvider(provider).build(pair.private)))
    }

    private fun emptyPreferences(): SharedPreferences {
        val pins = mutableMapOf<String, String>()
        val editor = Proxy.newProxyInstance(javaClass.classLoader, arrayOf(SharedPreferences.Editor::class.java)) { proxy, method, args ->
            when (method.name) {
                "putString" -> { pins[args!![0] as String] = args[1] as String; proxy }
                "commit" -> true
                else -> proxy
            }
        } as SharedPreferences.Editor
        return Proxy.newProxyInstance(javaClass.classLoader, arrayOf(SharedPreferences::class.java)) { _, method, args ->
            when (method.name) {
                "getString" -> pins[args!![0]]
                "edit" -> editor
                else -> null
            }
        } as SharedPreferences
    }
}
