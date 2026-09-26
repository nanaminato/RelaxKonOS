package app.relaxkonos.mobile.security

import androidx.fragment.app.FragmentActivity
import javax.crypto.Cipher
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * What [VaultAccess] answers when the platform refuses in a way the vault has no name for.
 *
 * `AndroidKeyStore` and `BiometricPrompt` can both throw outside the exceptions this package models —
 * a provider that fails with a runtime exception, a prompt the system declines to show at all. Such an
 * escape used to land in a caller coroutine that had no idea what to do with it: the sign-in form kept
 * its busy flag set, every field and the button disabled, for the rest of the process. The rule these
 * tests pin is that a vault operation always *answers*, and that an unnameable refusal is never turned
 * into a destroyed record or a dead key.
 *
 * [VaultAccess.loadWithoutPrompt] is the entry point that is fully exercisable in a JVM test: it takes
 * no activity, so a fake Keystore side is enough to make it fail. The two interactive paths share one
 * catch clause, which is [unnameableVaultFailure]; their own tests would need a real
 * `BiometricPrompt`, so what is verified here is the verdict that clause produces.
 */
class VaultAccessTest {
    @After
    fun tearDown() {
        VaultDiagnostics.sink = null
    }

    @Test
    fun `an unnameable platform failure answers instead of escaping`() {
        val outcome = access { IllegalStateException("Keystore is unavailable") }.loadWithoutPrompt(record())

        assertEquals(VaultOperation.Failed(UnlockFailure.Unknown), outcome)
    }

    @Test
    fun `an unnameable refusal is logged with its raw type`() {
        val events = mutableListOf<Pair<String, String?>>()
        VaultDiagnostics.sink = { event, detail -> events += event to detail }

        access { IllegalStateException("Keystore is unavailable") }.loadWithoutPrompt(record())

        assertEquals(
            listOf(
                "vault.load.unattended" to "kind=Connection reading inside an open window",
                "vault.load.unattended.unexpected" to "java.lang.IllegalStateException: Keystore is unavailable",
            ),
            events,
        )
    }

    @Test
    fun `a refusal the vault does know keeps its own verdict`() {
        assertEquals(
            VaultOperation.Failed(UnlockFailure.Tampered),
            access { VaultTamperException("payload failed authentication") }.loadWithoutPrompt(record()),
        )
        assertEquals(
            VaultOperation.Failed(UnlockFailure.KeyInvalidated),
            access { VaultKeyInvalidatedException("the key is gone") }.loadWithoutPrompt(record()),
        )
        assertEquals(
            VaultOperation.Failed(UnlockFailure.Unavailable),
            access { VaultKeyUnavailableException("not authorized right now") }.loadWithoutPrompt(record()),
        )
    }

    @Test
    fun `a record marked invalidated is refused before the key is touched`() {
        val outcome = access { error("the KeyStore must not be reached") }
            .loadWithoutPrompt(record(VaultRecordState.Invalidated))

        assertEquals(VaultOperation.Failed(UnlockFailure.KeyInvalidated), outcome)
    }

    @Test
    fun `the interactive paths answer with the same verdict`() {
        // Cancellation is rethrown ahead of this, so only real refusals arrive here.
        assertEquals(
            VaultOperation.Failed(UnlockFailure.Unknown),
            unnameableVaultFailure(operation = "save", error = IllegalStateException("boom")),
        )
        assertEquals(
            VaultOperation.Failed(UnlockFailure.Unknown),
            unnameableVaultFailure(operation = "load", error = IllegalStateException("boom")),
        )
    }

    @Test
    fun `the verdict for an unnameable refusal is one that never destroys anything`() {
        // The mapping must not reuse a failure whose advice contradicts it: "no longer valid" tells the
        // user their saved password is dead, and KeyInvalidated is what makes callers mark records.
        val mapped = unnameableVaultFailure(operation = "load", error = IllegalStateException("boom"))

        assertEquals(UnlockFailure.Unknown, mapped.failure)
    }

    private fun access(failWith: () -> Exception): VaultAccess = VaultAccess(
        CredentialVault(InMemoryVaultStorage(), ThrowingCrypto(failWith)),
        // Never reached on these paths: only `save` uses it, and only after the cipher step that throws.
        VaultKeyManager(),
        UnusedLock(),
    )

    private fun record(state: VaultRecordState = VaultRecordState.Sealed) = VaultRecord(
        kind = VaultKind.Connection,
        serviceId = "https://relaxkonos.local:5090",
        account = "nana",
        lastUsedEpochMillis = 1_000L,
        fingerprintProtected = true,
        iv = ByteArray(12),
        ciphertext = ByteArray(16),
        state = state,
    )

    /** The KeyStore stand-in: every operation fails with the error the test chose. */
    private class ThrowingCrypto(private val failWith: () -> Exception) : VaultCrypto {
        override fun sealCipher(kind: VaultKind): Cipher = throw failWith()

        override fun openCipher(kind: VaultKind, iv: ByteArray): Cipher = throw failWith()
    }

    /** Fails the test if a prompt is raised: none of these paths may need one. */
    private class UnusedLock : BiometricUnlock {
        override suspend fun authorize(
            activity: FragmentActivity,
            mode: VaultUnlockMode,
            cipher: Cipher?,
            title: String,
            subtitle: String,
            negativeButton: String,
        ): UnlockOutcome = error("no prompt may be raised by these paths")
    }
}
