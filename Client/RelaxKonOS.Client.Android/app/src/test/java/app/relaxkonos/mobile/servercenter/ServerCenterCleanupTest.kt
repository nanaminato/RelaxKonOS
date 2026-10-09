package app.relaxkonos.mobile.servercenter

import androidx.fragment.app.FragmentActivity
import app.relaxkonos.mobile.security.*
import javax.crypto.Cipher
import org.junit.Assert.*
import org.junit.Test

class ServerCenterCleanupTest {
    private fun coordinator(): Pair<ServerCenterCoordinator, ServerHostTarget> {
        val targets = ServerHostTargetStore(InMemoryHostTargetStorage())
        val target = targets.upsert(ServerHostTargetRules.create("test.invalid", 22, "test", null, 1L))
        val keys = ServerHostKeyTrustStore(InMemoryHostKeyStorage())
        val connections = DefaultServerCenterConnectionResolver(keys, targets, object : ServerCenterSshTransportFactory {
            override fun create(): ServerCenterSshTransport = error("No SSH connection is needed for cleanup")
        })
        val operations = ServerInstallOperationIndex(object : ServerInstallOperationStorage {
            override fun read(): ByteArray? = null
            override fun write(bytes: ByteArray) = error("No operation record is written")
        }, keys)
        val vault = CredentialVault(InMemoryVaultStorage(), object : VaultCrypto {
            override fun sealCipher(kind: VaultKind): Cipher = error("No secret is persisted")
            override fun openCipher(kind: VaultKind, iv: ByteArray): Cipher = error("No secret is unlocked")
        })
        val access = VaultAccess(vault, VaultKeyManager(), object : BiometricUnlock {
            override suspend fun authorize(activity: FragmentActivity, mode: VaultUnlockMode, cipher: Cipher?,
                title: String, subtitle: String, negativeButton: String): UnlockOutcome = error("No prompt is opened")
        })
        return ServerCenterCoordinator(targets, connections, keys, operations, ServerCenterSshCredentialStore(vault, access), { null }) to target
    }

    @Test fun failedWorkspaceCleanupStillInvalidatesItsIdentityAndRevision() {
        val (center, target) = coordinator()
        center.open()
        center.openSshFiles(target.hostId, "test-secret".toCharArray())
        val revision = center.workspaceRevision
        val failure = IllegalStateException("test cleanup failure")
        center.onWorkspaceClosed = { throw failure }
        assertTrue(runCatching { center.closeSshFiles() }.exceptionOrNull() === failure)
        assertNull(center.sshFilesHostId)
        assertEquals(revision + 1, center.workspaceRevision)
        assertTrue(center.hasSessionPassword(target.hostId))
    }

    @Test fun closingCenterAlwaysClearsSessionCredentialsEvenWhenChildCleanupThrows() {
        val (center, target) = coordinator()
        center.open()
        center.openSshFiles(target.hostId, "test-secret".toCharArray())
        center.onWorkspaceClosed = { throw IllegalStateException("test cleanup failure") }
        assertNotNull(runCatching { center.close() }.exceptionOrNull())
        assertFalse(center.isOpen)
        assertNull(center.sshFilesHostId)
        assertFalse(center.hasSessionPassword(target.hostId))
        assertNull(center.verifiedPasswordCopy(target.hostId))
    }
}
