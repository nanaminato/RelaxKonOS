package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class OperationIndexTest {
    private class MemoryStorage : OperationIndexStorage {
        var bytes: ByteArray? = null
        override fun read(): ByteArray? = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }

    private fun owner(service: String, account: String) = SessionState.Active(
        service, "http://127.0.0.1:20000", account, "workspace", emptySet(), "linux",
        ExecutionEligibility(true, null, true),
    )

    @Test
    fun `references survive restart and remain isolated by server and account`() {
        val storage = MemoryStorage()
        val index = OperationIndex(storage)
        val alice = owner("installation-1", "alice")
        val bob = owner("installation-1", "bob")
        val otherServer = owner("installation-2", "alice")
        index.record(alice, OperationDomain.Deployment, "app-1", "op-1")
        index.record(bob, OperationDomain.Website, "app-2", "op-2")
        index.record(otherServer, OperationDomain.Deployment, "app-3", "op-3")

        val restored = OperationIndex(storage)
        assertEquals(listOf("op-1"), restored.forOwner(alice).map { it.operationId })
        assertEquals(listOf("op-2"), restored.forOwner(bob).map { it.operationId })
        assertEquals(listOf("op-3"), restored.forOwner(otherServer).map { it.operationId })
        restored.hide(alice, restored.forOwner(alice).single())
        assertTrue(restored.forOwner(alice).isEmpty())
        restored.record(alice, OperationDomain.Deployment, "app-1", "op-1")
        assertTrue(restored.isHidden(alice, OperationDomain.Deployment, "op-1"))
        assertEquals(1, restored.forOwner(bob).size)
    }

    @Test
    fun `rediscovery does not duplicate an operation or change its first seen time`() {
        val index = OperationIndex(MemoryStorage())
        val owner = owner("installation-1", "alice")
        index.record(owner, OperationDomain.Deployment, "app-1", "op-1")
        val original = index.forOwner(owner).single()
        index.record(owner, OperationDomain.Deployment, "app-1", "op-1")
        assertEquals(listOf(original), index.forOwner(owner))
    }

    @Test
    fun `compose git and script references remain distinct and hidden records stay hidden`() {
        val storage = MemoryStorage()
        val index = OperationIndex(storage)
        val alice = owner("installation-1", "alice")
        val bob = owner("installation-1", "bob")
        listOf(OperationDomain.Compose, OperationDomain.GitBuild, OperationDomain.Script)
            .forEach { index.record(alice, it, "resource-${it.name}", "shared-id") }
        assertEquals(3, OperationIndex(storage).forOwner(alice).size)
        assertTrue(OperationIndex(storage).forOwner(bob).isEmpty())

        val compose = index.forOwner(alice).single { it.domain == OperationDomain.Compose }
        index.markVerified(alice, OperationDomain.Compose, compose.operationId, 1234L)
        index.hide(alice, compose)
        index.record(alice, OperationDomain.Compose, compose.resourceId, compose.operationId)
        val restored = OperationIndex(storage)
        assertTrue(restored.isHidden(alice, OperationDomain.Compose, "shared-id"))
        assertEquals(setOf(OperationDomain.GitBuild, OperationDomain.Script),
            restored.forOwner(alice).map { it.domain }.toSet())
    }

    @Test
    fun `invalid persisted bytes do not expose another account or block reading`() {
        val storage = MemoryStorage()
        storage.bytes = byteArrayOf(1, 2, 3)
        assertTrue(OperationIndex(storage).forOwner(owner("installation-1", "alice")).isEmpty())
    }
}
