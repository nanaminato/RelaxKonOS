package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import org.junit.Assert.*
import org.junit.Test

class InstallationRequestJournalTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private fun owner(host: String = "a", account: String = "alice") = SessionState.Active(
        host, "https://example.test", account, "workspace", setOf(ServerCapabilities.WEB_SERVER), "linux",
        ExecutionEligibility(true, null, false), true)

    @Test fun `unknown request survives restart with original key and owner isolation`() {
        val storage = Storage()
        val first = InstallationRequestJournal(storage).begin(owner(), InstallationService.Nginx, InstallationKind.Install, "digest")
        InstallationRequestJournal(storage).update(first.copy(attempted = true))
        val restored = InstallationRequestJournal(storage)
        assertEquals(first.key, restored.begin(owner(), first.service, first.kind, first.fingerprint).key)
        assertTrue(restored.pending(owner()).single().attempted)
        assertTrue(restored.pending(owner(account = "bob")).isEmpty())
        assertTrue(restored.pending(owner(host = "b")).isEmpty())
    }

    @Test fun `changed options cannot replace an unresolved request`() {
        val journal = InstallationRequestJournal(Storage())
        journal.begin(owner(), InstallationService.Frp, InstallationKind.Install, "first")
        assertTrue(runCatching { journal.begin(owner(), InstallationService.Frp, InstallationKind.Install, "different") }.isFailure)
    }

    @Test fun `known ID persists before completion and completion is scoped`() {
        val journal = InstallationRequestJournal(Storage())
        val alice = journal.begin(owner(), InstallationService.Nginx, InstallationKind.Install, "digest")
        val bob = journal.begin(owner(account = "bob"), InstallationService.Nginx, InstallationKind.Install, "digest")
        journal.update(alice.copy(attempted = true, operationId = "00112233-4455-6677-8899-aabbccddeeff"))
        assertNotNull(journal.pending(owner()).single().operationId)
        journal.complete(alice)
        assertTrue(journal.pending(owner()).isEmpty())
        assertEquals(bob, journal.pending(owner(account = "bob")).single())
    }

    @Test fun `corrupt journal fails closed instead of allocating a new submission key`() {
        val storage = Storage().also { it.bytes = byteArrayOf(1, 2, 3) }
        assertTrue(runCatching { InstallationRequestJournal(storage).begin(owner(), InstallationService.Nginx, InstallationKind.Install, "digest") }.isFailure)
    }
}
