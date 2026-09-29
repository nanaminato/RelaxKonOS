package app.relaxkonos.mobile.servercenter

import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ServerInstallOperationIndexTest {
    private val target = ServerHostTargetRules.create("host.example", 22, "deploy", null, 1L)
    private val otherUser = ServerHostTargetRules.create("host.example", 22, "other", null, 1L)
    private val key = ServerCenterHostKeyObservation("host.example", 22, "ssh-ed25519", byteArrayOf(1, 2, 3))
    private val changedKey = ServerCenterHostKeyObservation("host.example", 22, "ssh-ed25519", byteArrayOf(4, 5, 6))

    @Test
    fun `references survive index recreation but stay scoped to target and pinned key`() {
        val storage = MemoryInstallStorage()
        val trust = ServerHostKeyTrustStore(InMemoryHostKeyStorage())
        pin(trust, key)
        val first = ServerInstallOperationIndex(storage, trust)
        val id = UUID.randomUUID().toString()
        val reference = first.record(target, key, id, ServerHostPlatform.Linux)
        assertEquals(reference, first.record(target, key, id, ServerHostPlatform.Linux))

        val reopened = ServerInstallOperationIndex(storage, trust)
        assertEquals(listOf(reference), reopened.forTrustedHost(target, key))
        assertTrue(reopened.forTrustedHost(otherUser, key).isEmpty())
        reopened.markVerified(target, reference, key, 99L)
        reopened.markVerified(target, reference, key, 100L)
        assertEquals(100L, reopened.forTrustedHost(target, key).single().lastVerifiedAtMillis)

        pin(trust, changedKey)
        assertTrue(rejected { reopened.forTrustedHost(target, key) })
        assertTrue(reopened.forTrustedHost(target, changedKey).isEmpty())
        assertTrue(rejected { reopened.markVerified(target, reference, key, 101L) })
    }

    @Test
    fun `untrusted or mismatched host cannot record an install operation`() {
        val trust = ServerHostKeyTrustStore(InMemoryHostKeyStorage())
        val storage = MemoryInstallStorage()
        val index = ServerInstallOperationIndex(storage, trust)
        val id = UUID.randomUUID().toString()
        assertTrue(rejected { index.record(target, key, id, ServerHostPlatform.Linux) })
        pin(trust, key)
        assertTrue(rejected {
            index.record(target, ServerCenterHostKeyObservation("other.example", 22, key.algorithm,
                key.publicKeyBlob), id, ServerHostPlatform.Linux)
        })
        assertTrue(rejected { index.record(target, key, "bad; id", ServerHostPlatform.Linux) })
        assertNull(storage.read())
    }

    @Test
    fun `corrupt references fail closed`() {
        val storage = MemoryInstallStorage()
        val trust = ServerHostKeyTrustStore(InMemoryHostKeyStorage())
        pin(trust, key)
        val index = ServerInstallOperationIndex(storage, trust)
        storage.write(byteArrayOf(1, 2, 3))
        assertTrue(index.forTrustedHost(target, key).isEmpty())
        storage.write(ByteArray(65 * 1024))
        assertTrue(index.forTrustedHost(target, key).isEmpty())
    }

    @Test
    fun `forgetting one host does not remove another user on the same SSH endpoint`() {
        val storage = MemoryInstallStorage()
        val trust = ServerHostKeyTrustStore(InMemoryHostKeyStorage())
        pin(trust, key)
        val index = ServerInstallOperationIndex(storage, trust)
        index.record(target, key, UUID.randomUUID().toString(), ServerHostPlatform.Linux)
        val otherReference = index.record(otherUser, key, UUID.randomUUID().toString(), ServerHostPlatform.Linux)
        index.forgetHost(target.hostId)
        assertTrue(index.forTrustedHost(target, key).isEmpty())
        assertEquals(listOf(otherReference), index.forTrustedHost(otherUser, key))
    }

    private fun pin(trust: ServerHostKeyTrustStore, observed: ServerCenterHostKeyObservation) {
        trust.trust(ServerCenterSshEndpoint.create(observed.host, observed.port, "deploy"), observed, 1L)
    }

    private fun rejected(block: () -> Unit): Boolean = try {
        block()
        false
    } catch (_: IllegalArgumentException) {
        true
    }
}

private class MemoryInstallStorage : ServerInstallOperationStorage {
    private var bytes: ByteArray? = null
    override fun read(): ByteArray? = bytes?.copyOf()
    override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
}
