package app.relaxkonos.mobile.servercenter

import org.junit.*
import org.junit.Assert.*
import org.junit.rules.TemporaryFolder
import java.io.IOException
import java.util.UUID

class MaintenancePendingJournalTest {
    @get:Rule val directory = TemporaryFolder()
    private class Memory : MaintenancePendingStorage {
        var bytes: ByteArray? = null
        var fail = false
        override fun read() = bytes?.copyOf()
        override fun write(bytes: ByteArray) { if (fail) throw IOException("private storage"); this.bytes = bytes.copyOf() }
    }
    private fun pending(name: String = "original.invalid") = MaintenancePending(ServerInstallOperationReference(
        ServerHostTargetRules.hostId(name, 22, "review"), "ssh-ed25519",
        ServerCenterHostKeyObservation(name, 22, "ssh-ed25519", byteArrayOf(1, 2, 3)).fingerprint,
        UUID.randomUUID().toString(), ServerHostPlatform.Linux, 1), ServerDeploymentKind.Repair)
    private fun rejected(action: () -> Unit): Boolean = try { action(); false } catch (_: Exception) { true }
    @Test fun `real file survives journal recreation and resolves only original metadata`() {
        val path = directory.newFolder(); val original = pending(); val other = pending("other.invalid")
        MaintenancePendingJournal(FileMaintenancePendingStorage(path)).apply { record(original); record(other) }
        val reopened = MaintenancePendingJournal(FileMaintenancePendingStorage(path))
        assertEquals(original, reopened.load(original.reference.hostId))
        assertEquals(other, reopened.load(other.reference.hostId))
        assertTrue(rejected { reopened.resolve(original.copy(kind = ServerDeploymentKind.Uninstall)) })
        reopened.resolve(original)
        val again = MaintenancePendingJournal(FileMaintenancePendingStorage(path))
        assertNull(again.load(original.reference.hostId)); assertEquals(other, again.load(other.reference.hostId))
    }
    @Test fun `corruption truncation trailing data and wrong version never mean an empty journal`() {
        val memory = Memory(); val original = pending(); val journal = MaintenancePendingJournal(memory)
        journal.record(original); val valid = requireNotNull(memory.bytes)
        listOf(byteArrayOf(), valid.copyOf(valid.size - 1), valid + byteArrayOf(0), valid.copyOf().also { it[0] = 0 }, ByteArray(65537)).forEach {
            memory.bytes = it; assertTrue(rejected { journal.load(original.reference.hostId) })
        }
    }
    @Test fun `invalid metadata and replacement cannot overwrite an unresolved original`() {
        val memory = Memory(); val journal = MaintenancePendingJournal(memory); val original = pending()
        assertTrue(rejected { journal.record(original.copy(kind = ServerDeploymentKind.Status)) }); assertNull(memory.bytes)
        assertTrue(rejected { journal.record(original.copy(reference = original.reference.copy(hostKeyFingerprint = "invalid"))) })
        journal.record(original); val bytes = memory.bytes!!.copyOf()
        assertTrue(rejected { journal.record(original.copy(reference = original.reference.copy(operationId = UUID.randomUUID().toString()))) })
        assertArrayEquals(bytes, memory.bytes); assertEquals(original, journal.load(original.reference.hostId))
    }
    @Test fun `write failure preserves previously committed journal`() {
        val memory = Memory(); val journal = MaintenancePendingJournal(memory); val original = pending()
        journal.record(original); val before = memory.bytes!!.copyOf(); memory.fail = true
        assertTrue(rejected { journal.resolve(original) }); assertArrayEquals(before, memory.bytes)
        assertEquals(original, MaintenancePendingJournal(memory).load(original.reference.hostId))
    }
    @Test fun `capacity never evicts an unresolved operation`() {
        val memory = Memory(); val journal = MaintenancePendingJournal(memory)
        val values = (0 until 100).map { pending("host-$it.invalid") }; values.forEach(journal::record)
        assertTrue(rejected { journal.record(pending("overflow.invalid")) })
        assertEquals(values.first(), journal.load(values.first().reference.hostId))
    }
}
