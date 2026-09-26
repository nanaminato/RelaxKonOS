package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** 主机密钥固定规则的契约测试。与 C# `ServerHostTrustRules` 必须给出同一结论。 */
class ServerHostTrustTest {

    private val keyA = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)
    private val keyB = byteArrayOf(9, 9, 9, 9, 9, 9, 9, 9)

    private fun record(
        host: String = "host.example.com",
        port: Int = 22,
        algorithm: String = "ssh-ed25519",
        blob: ByteArray = keyA,
    ) = ServerHostKeyRecord(
        host = host,
        port = port,
        algorithm = algorithm,
        publicKeyBase64 = Base64Codec.encode(blob),
        fingerprint = ServerHostTrustRules.fingerprint(blob),
        confirmedAtEpochMillis = 0L,
    )

    @Test
    fun `fingerprint is openssh shaped and stable`() {
        val fingerprint = ServerHostTrustRules.fingerprint(keyA)
        assertTrue(ServerHostTrustRules.isFingerprint(fingerprint))
        assertEquals(ServerHostTrustRules.FINGERPRINT_PREFIX.length + 43, fingerprint.length)
        assertEquals(fingerprint, ServerHostTrustRules.fingerprint(keyA))
        assertFalse(fingerprint == ServerHostTrustRules.fingerprint(keyB))
        assertFalse(ServerHostTrustRules.isFingerprint("MD5:aa:bb"))
        assertFalse(ServerHostTrustRules.isFingerprint(null))
    }

    @Test
    fun `grouped fingerprint separates every four characters`() {
        val grouped = ServerHostTrustRules.groupedFingerprint(ServerHostTrustRules.fingerprint(keyA))
        assertTrue(grouped.startsWith(ServerHostTrustRules.FINGERPRINT_PREFIX))
        assertTrue(grouped.contains(' '))
    }

    @Test
    fun `endpoint key normalizes host case and ipv6 brackets`() {
        assertEquals("host.example.com:22", ServerHostTrustRules.endpointKey("Host.Example.COM", 22))
        assertEquals("::1:2222", ServerHostTrustRules.endpointKey("[::1]", 2222))
        assertEquals("node-1", ServerHostTrustRules.normalizeHost("  Node-1 "))
    }

    @Test
    fun `first contact requires confirmation instead of trusting silently`() {
        assertEquals(
            ServerHostKeyTrust.Unknown,
            ServerHostTrustRules.evaluate(emptyList(), "host.example.com", 22, "ssh-ed25519", keyA),
        )
        assertEquals(
            ServerDeploymentProblemCodes.HOST_KEY_UNKNOWN,
            ServerHostTrustRules.problemCode(ServerHostKeyTrust.Unknown),
        )
    }

    @Test
    fun `matching pinned key is trusted regardless of host case`() {
        val known = listOf(record())
        assertEquals(
            ServerHostKeyTrust.Trusted,
            ServerHostTrustRules.evaluate(known, "HOST.example.com", 22, "ssh-ed25519", keyA),
        )
        assertNull(ServerHostTrustRules.problemCode(ServerHostKeyTrust.Trusted))
    }

    @Test
    fun `changed key is reported as changed and blocks writes`() {
        val known = listOf(record())
        assertEquals(
            ServerHostKeyTrust.Changed,
            ServerHostTrustRules.evaluate(known, "host.example.com", 22, "ssh-ed25519", keyB),
        )
        assertEquals(
            ServerDeploymentProblemCodes.HOST_KEY_CHANGED,
            ServerHostTrustRules.problemCode(ServerHostKeyTrust.Changed),
        )
        assertTrue(ServerHostTrustRules.blocksWriteOperations(ServerHostKeyTrust.Changed))
        assertFalse(ServerHostTrustRules.blocksWriteOperations(ServerHostKeyTrust.Unknown))
        assertFalse(ServerHostTrustRules.blocksWriteOperations(ServerHostKeyTrust.Trusted))
    }

    @Test
    fun `different port and different algorithm are separate pins`() {
        val known = listOf(record())
        assertEquals(
            ServerHostKeyTrust.Unknown,
            ServerHostTrustRules.evaluate(known, "host.example.com", 2222, "ssh-ed25519", keyA),
        )
        assertEquals(
            ServerHostKeyTrust.Unknown,
            ServerHostTrustRules.evaluate(known, "host.example.com", 22, "rsa-sha2-256", keyA),
        )
    }

    @Test
    fun `confirming a rotated key replaces the previous pin`() {
        val replaced = ServerHostTrustRules.replace(listOf(record()), record(blob = keyB))
        assertEquals(1, replaced.size)
        assertEquals(
            ServerHostKeyTrust.Trusted,
            ServerHostTrustRules.evaluate(replaced, "host.example.com", 22, "ssh-ed25519", keyB),
        )
        assertNotNull(ServerHostTrustRules.find(replaced, "host.example.com", 22, "ssh-ed25519"))
    }
}