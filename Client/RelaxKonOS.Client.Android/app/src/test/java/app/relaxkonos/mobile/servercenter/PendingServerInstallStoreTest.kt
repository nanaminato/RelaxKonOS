package app.relaxkonos.mobile.servercenter

import java.io.File
import java.util.UUID
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class PendingServerInstallStoreTest {
    @get:Rule val temporary = TemporaryFolder()
    private fun pending(user: String = "deploy") = PendingServerInstall(
        ServerInstallOperationReference(ServerHostTargetRules.create("host.example", 22, user, null, 1L).hostId,
            "ssh-ed25519", ServerCenterHostKeyObservation("host.example", 22, "ssh-ed25519", byteArrayOf(1, 2, 3)).fingerprint,
            UUID.randomUUID().toString(), ServerHostPlatform.Linux, 1L), ServerInstallMode.LinuxSystem,
        ServerDeploymentKind.Upgrade, true)

    @Test fun recreatedStoreRestoresExactOperationAndScopesOtherSshUsers() {
        val root = temporary.newFolder()
        val original = pending()
        PendingServerInstallStore(root).write(original)
        val reopened = PendingServerInstallStore(root)
        assertEquals(original, reopened.read(original.reference.hostId))
        assertNull(reopened.read(pending("other").reference.hostId))
        reopened.clear(original)
        assertNull(PendingServerInstallStore(root).read(original.reference.hostId))
    }

    @Test fun anotherOperationOrKeyCannotOverwriteOrClearUnresolvedInstall() {
        val root = temporary.newFolder()
        val store = PendingServerInstallStore(root)
        val original = pending()
        store.write(original)
        store.write(original)
        val other = original.copy(reference = original.reference.copy(operationId = UUID.randomUUID().toString()))
        assertThrows(IllegalArgumentException::class.java) { store.write(other) }
        assertThrows(IllegalArgumentException::class.java) { store.clear(other) }
        val changedKey = original.copy(reference = original.reference.copy(hostKeyFingerprint =
            ServerCenterHostKeyObservation("host.example", 22, "ssh-ed25519", byteArrayOf(4, 5, 6)).fingerprint))
        assertThrows(IllegalArgumentException::class.java) { store.clear(changedKey) }
        assertEquals(original, store.read(original.reference.hostId))
    }

    @Test fun corruptOrUnsupportedRecordIsNotTreatedAsNoPendingInstall() {
        val root = temporary.newFolder()
        val store = PendingServerInstallStore(root)
        val original = pending()
        store.write(original)
        val record = File(root, "pending-server-install-${original.reference.hostId}.bin")
        val bytes = record.readBytes()
        record.writeBytes(bytes + byteArrayOf(1))
        assertThrows(IllegalArgumentException::class.java) { store.read(original.reference.hostId) }
        record.writeBytes(byteArrayOf(0, 0, 0, 0))
        assertThrows(IllegalArgumentException::class.java) { store.read(original.reference.hostId) }
        record.writeBytes(ByteArray(8193))
        assertThrows(IllegalArgumentException::class.java) { store.read(original.reference.hostId) }
    }

    @Test fun persistenceFailureCannotCreateAUsablePendingRecord() {
        val root = temporary.newFile()
        val store = PendingServerInstallStore(root)
        assertThrows(Exception::class.java) { store.write(pending()) }
        assertTrue(root.isFile)
    }

    @Test fun dispatchStageSurvivesRecreationAndCannotBeResetOrClearedByUnsentSnapshot() {
        val root = temporary.newFolder()
        val store = PendingServerInstallStore(root)
        val reserved = pending()
        store.write(reserved)
        val dispatched = store.markStarted(reserved)
        assertTrue(dispatched.attempted)
        val reopened = PendingServerInstallStore(root)
        assertEquals(dispatched, reopened.read(reserved.reference.hostId))
        assertThrows(IllegalArgumentException::class.java) { reopened.write(reserved) }
        assertThrows(IllegalArgumentException::class.java) { reopened.clear(reserved) }
        assertThrows(IllegalArgumentException::class.java) { reopened.markStarted(reserved) }
        reopened.clear(dispatched)
        assertNull(reopened.read(reserved.reference.hostId))
    }

    @Test fun oldJournalFormatCannotBeAcceptedAsAnUnsentOperation() {
        val root = temporary.newFolder()
        val store = PendingServerInstallStore(root)
        val reserved = pending()
        store.write(reserved)
        val record = File(root, "pending-server-install-${reserved.reference.hostId}.bin")
        val oldBytes = record.readBytes().dropLast(1).toByteArray()
        oldBytes[3] = 0x31
        record.writeBytes(oldBytes)
        assertThrows(IllegalArgumentException::class.java) { store.read(reserved.reference.hostId) }
    }
}
