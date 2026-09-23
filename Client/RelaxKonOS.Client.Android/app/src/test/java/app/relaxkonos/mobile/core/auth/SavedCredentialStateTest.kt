package app.relaxkonos.mobile.core.auth

import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultRecord
import app.relaxkonos.mobile.security.VaultRecordState
import app.relaxkonos.mobile.security.VaultUnlockMode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * The four states of a saved credential, and the status line each one produces
 * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §4, §6.2).
 *
 * The distinction that matters most is `Unavailable` versus `Invalidated`: reporting a temporarily
 * unusable credential as a dead one makes the user save it again for no reason, while reporting a dead
 * one as merely unusable leaves them retrying something that can never work.
 */
class SavedCredentialStateTest {
    private fun record(state: VaultRecordState = VaultRecordState.Sealed) = VaultRecord(
        kind = VaultKind.Connection,
        serverUrl = "https://relaxkonos.local:5090",
        account = "nana",
        lastUsedEpochMillis = 1_000L,
        fingerprintProtected = true,
        iv = ByteArray(12),
        ciphertext = ByteArray(16),
        state = state,
    )

    @Test
    fun `no record at all is absent`() {
        assertEquals(SavedCredentialState.Absent, credentialState(null, null))
        assertEquals(SavedCredentialState.Absent, credentialState(null, VaultUnlockMode.PerUseStrongBiometric))
    }

    @Test
    fun `a sealed record with an unlock path is available`() {
        assertEquals(SavedCredentialState.Available, credentialState(record(), VaultUnlockMode.PerUseStrongBiometric))
        assertEquals(SavedCredentialState.Available, credentialState(record(), VaultUnlockMode.DeviceUnlockWindow))
    }

    @Test
    fun `a sealed record with no unlock path is unavailable rather than absent`() {
        assertEquals(SavedCredentialState.Unavailable, credentialState(record(), null))
    }

    @Test
    fun `an invalidated record outranks a missing unlock path`() {
        val invalidated = record(VaultRecordState.Invalidated)

        assertEquals(SavedCredentialState.Invalidated, credentialState(invalidated, null))
        assertEquals(
            SavedCredentialState.Invalidated,
            credentialState(invalidated, VaultUnlockMode.PerUseStrongBiometric),
        )
    }

    @Test
    fun `unavailable and invalidated are never the same answer`() {
        assertNotEquals(SavedCredentialState.Unavailable, SavedCredentialState.Invalidated)
    }

    @Test
    fun `the status line follows the state and the unlock mode`() {
        assertEquals(CredentialStatus.None, credentialStatus(SavedCredentialState.Absent, null))
        assertEquals(
            CredentialStatus.SavedByFingerprint,
            credentialStatus(SavedCredentialState.Available, VaultUnlockMode.PerUseStrongBiometric),
        )
        assertEquals(
            CredentialStatus.SavedByScreenLock,
            credentialStatus(SavedCredentialState.Available, VaultUnlockMode.DeviceUnlockWindow),
        )
        assertEquals(CredentialStatus.Unavailable, credentialStatus(SavedCredentialState.Unavailable, null))
        assertEquals(CredentialStatus.Invalidated, credentialStatus(SavedCredentialState.Invalidated, null))
    }
}
