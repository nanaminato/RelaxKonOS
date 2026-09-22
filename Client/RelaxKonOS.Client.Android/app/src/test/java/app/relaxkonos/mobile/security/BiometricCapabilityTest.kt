package app.relaxkonos.mobile.security

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The four-state authenticator mapping and the vault gating decisions D1-D3 of
 * `RelaxKonOS.Mobile.V1.Design.md` §5.6. These are product decisions, so they are asserted rather than
 * left to whichever screen happens to call the mapping.
 */
class BiometricCapabilityTest {
    @Test
    fun `strong wins over every weaker authenticator`() {
        assertEquals(BiometricCapability.Strong, capabilityFor(strongAvailable = true, weakAvailable = true, deviceCredentialAvailable = true))
        assertEquals(BiometricCapability.Strong, capabilityFor(strongAvailable = true, weakAvailable = false, deviceCredentialAvailable = false))
    }

    @Test
    fun `weak is used when strong is missing`() {
        assertEquals(BiometricCapability.WeakOnly, capabilityFor(strongAvailable = false, weakAvailable = true, deviceCredentialAvailable = true))
    }

    @Test
    fun `device credential is the last resort`() {
        assertEquals(BiometricCapability.DeviceCredentialOnly, capabilityFor(false, false, true))
    }

    @Test
    fun `no lock screen yields none`() {
        assertEquals(BiometricCapability.None, capabilityFor(false, false, false))
    }

    @Test
    fun `the connection vault is refused only without a lock screen`() {
        assertTrue(BiometricCapability.Strong.allowsConnectionVault)
        assertTrue(BiometricCapability.WeakOnly.allowsConnectionVault)
        assertTrue(BiometricCapability.DeviceCredentialOnly.allowsConnectionVault)
        assertFalse(BiometricCapability.None.allowsConnectionVault)
    }

    @Test
    fun `only a strong biometric may protect a saved administrator password`() {
        assertTrue(BiometricCapability.Strong.allowsElevationVault)
        assertFalse(BiometricCapability.WeakOnly.allowsElevationVault)
        assertFalse(BiometricCapability.DeviceCredentialOnly.allowsElevationVault)
        assertFalse(BiometricCapability.None.allowsElevationVault)
    }

    @Test
    fun `reduced strength is reported for weak biometrics and device credentials`() {
        assertFalse(BiometricCapability.Strong.isReducedStrength)
        assertTrue(BiometricCapability.WeakOnly.isReducedStrength)
        assertTrue(BiometricCapability.DeviceCredentialOnly.isReducedStrength)
        assertFalse(BiometricCapability.None.isReducedStrength)
    }

    @Test
    fun `connection vault unlock mode follows the capability`() {
        assertEquals(
            VaultUnlockMode.PerUseStrongBiometric,
            unlockModeFor(VaultKind.Connection, BiometricCapability.Strong, deviceUnlockWindowEnabled = true),
        )
        assertEquals(
            VaultUnlockMode.DeviceUnlockWindow,
            unlockModeFor(VaultKind.Connection, BiometricCapability.WeakOnly, deviceUnlockWindowEnabled = true),
        )
        assertEquals(
            VaultUnlockMode.DeviceUnlockWindow,
            unlockModeFor(VaultKind.Connection, BiometricCapability.DeviceCredentialOnly, deviceUnlockWindowEnabled = true),
        )
        assertNull(unlockModeFor(VaultKind.Connection, BiometricCapability.None, deviceUnlockWindowEnabled = true))
    }

    @Test
    fun `the elevation vault only ever accepts a strong per-use biometric`() {
        assertEquals(
            VaultUnlockMode.PerUseStrongBiometric,
            unlockModeFor(VaultKind.Elevation, BiometricCapability.Strong, deviceUnlockWindowEnabled = true),
        )
        assertNull(unlockModeFor(VaultKind.Elevation, BiometricCapability.WeakOnly, deviceUnlockWindowEnabled = true))
        assertNull(unlockModeFor(VaultKind.Elevation, BiometricCapability.DeviceCredentialOnly, deviceUnlockWindowEnabled = true))
        assertNull(unlockModeFor(VaultKind.Elevation, BiometricCapability.None, deviceUnlockWindowEnabled = true))
    }

    @Test
    fun `turning the device window off disables the fallback but not the strong path`() {
        assertNull(unlockModeFor(VaultKind.Connection, BiometricCapability.WeakOnly, deviceUnlockWindowEnabled = false))
        assertNull(unlockModeFor(VaultKind.Connection, BiometricCapability.DeviceCredentialOnly, deviceUnlockWindowEnabled = false))
        assertEquals(
            VaultUnlockMode.PerUseStrongBiometric,
            unlockModeFor(VaultKind.Connection, BiometricCapability.Strong, deviceUnlockWindowEnabled = false),
        )
    }

    @Test
    fun `device credential is the option that needs an explicit opt-in`() {
        assertTrue(BiometricCapability.DeviceCredentialOnly.requiresExplicitOptIn)
        assertFalse(BiometricCapability.Strong.requiresExplicitOptIn)
        assertFalse(BiometricCapability.WeakOnly.requiresExplicitOptIn)
        assertFalse(BiometricCapability.None.requiresExplicitOptIn)
    }
}
