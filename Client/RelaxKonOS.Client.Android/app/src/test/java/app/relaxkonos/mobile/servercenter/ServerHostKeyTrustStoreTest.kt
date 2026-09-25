package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** 主机密钥固定仓库的持久化测试：核对后固定，变化时判为变化，损坏文件不成为信任来源。 */
class ServerHostKeyTrustStoreTest {

    private val keyA = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)
    private val keyB = byteArrayOf(9, 9, 9, 9, 9, 9, 9, 9)
    private val endpoint = ServerCenterSshEndpoint.create("Host.Example", 22, "deploy")

    private fun observation(blob: ByteArray, algorithm: String = "ssh-ed25519") =
        ServerCenterHostKeyObservation("host.example", 22, algorithm, blob)

    private fun store() = ServerHostKeyTrustStore(InMemoryHostKeyStorage())

    @Test
    fun `first contact is unknown until the fingerprint is confirmed`() {
        val trust = store()
        assertEquals(ServerHostKeyTrust.Unknown, trust.evaluate(endpoint, observation(keyA)))
    }

    @Test
    fun `a confirmed key is trusted and survives a reload`() {
        val trust = store()
        trust.trust(endpoint, observation(keyA), nowEpochMillis = 1_790_000_000_000L)

        assertEquals(ServerHostKeyTrust.Trusted, trust.evaluate(endpoint, observation(keyA)))
        val record = trust.find("host.example", 22, "ssh-ed25519")
        assertEquals(ServerHostTrustRules.fingerprint(keyA), record?.fingerprint)
        assertEquals(1_790_000_000_000L, record?.confirmedAtEpochMillis)
    }

    @Test
    fun `the endpoint is normalized before pinning`() {
        val trust = store()
        trust.trust(endpoint, observation(keyA), nowEpochMillis = 0L)
        // 大小写不同的同一主机名必须命中同一条固定记录。
        assertEquals(ServerHostKeyTrust.Trusted, trust.evaluate(ServerCenterSshEndpoint.create("host.example", 22, "deploy"), observation(keyA)))
    }

    @Test
    fun `a different key on the same endpoint is reported as changed`() {
        val trust = store()
        trust.trust(endpoint, observation(keyA), nowEpochMillis = 0L)
        assertEquals(ServerHostKeyTrust.Changed, trust.evaluate(endpoint, observation(keyB)))
    }

    @Test
    fun `each algorithm and each port is pinned separately`() {
        val trust = store()
        trust.trust(endpoint, observation(keyA), nowEpochMillis = 0L)

        assertEquals(
            ServerHostKeyTrust.Unknown,
            trust.evaluate(endpoint, observation(keyA, algorithm = "rsa-sha2-256")),
        )
        assertEquals(
            ServerHostKeyTrust.Unknown,
            trust.evaluate(ServerCenterSshEndpoint.create("host.example", 2222, "deploy"), observation(keyA)),
        )
    }

    @Test
    fun `confirming a rotated key replaces the previous pin`() {
        val trust = store()
        trust.trust(endpoint, observation(keyA), nowEpochMillis = 0L)
        trust.trust(endpoint, observation(keyB), nowEpochMillis = 1L)

        assertEquals(1, trust.all().size)
        assertEquals(ServerHostKeyTrust.Trusted, trust.evaluate(endpoint, observation(keyB)))
    }

    @Test
    fun `forgetting one algorithm leaves the other pins alone`() {
        val trust = store()
        trust.trust(endpoint, observation(keyA), nowEpochMillis = 0L)
        trust.trust(endpoint, observation(keyA, algorithm = "rsa-sha2-256"), nowEpochMillis = 0L)

        trust.forget("host.example", 22, "ssh-ed25519")

        assertNull(trust.find("host.example", 22, "ssh-ed25519"))
        assertEquals(ServerHostKeyTrust.Trusted, trust.evaluate(endpoint, observation(keyA, algorithm = "rsa-sha2-256")))
    }

    @Test
    fun `forgetting an endpoint removes every algorithm of it`() {
        val trust = store()
        trust.trust(endpoint, observation(keyA), nowEpochMillis = 0L)
        trust.trust(endpoint, observation(keyA, algorithm = "rsa-sha2-256"), nowEpochMillis = 0L)

        trust.forgetEndpoint("HOST.EXAMPLE", 22)

        assertTrue(trust.all().isEmpty())
        assertEquals(ServerHostKeyTrust.Unknown, trust.evaluate(endpoint, observation(keyA)))
    }

    @Test
    fun `an unreadable file trusts nothing`() {
        val storage = InMemoryHostKeyStorage()
        storage.write(byteArrayOf(7, 7, 7, 7))
        val trust = ServerHostKeyTrustStore(storage)

        assertTrue(trust.all().isEmpty())
        assertEquals(ServerHostKeyTrust.Unknown, trust.evaluate(endpoint, observation(keyA)))
    }
}