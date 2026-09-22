package app.relaxkonos.mobile.security

import android.content.Context
import androidx.biometric.BiometricManager

/**
 * Result of probing the device for a usable authenticator. The set is deliberately exhaustive:
 * the UI renders one of four shapes and never guesses whether biometrics "might" work.
 *
 * `RelaxKonOS.Mobile.V1.Design.md` §5.6 fixes which vault each result unlocks. Those product
 * decisions (D2, D3) are not implementation options, so they are encoded here instead of being
 * re-derived in each screen.
 */
enum class BiometricCapability {
    /** Strong biometrics (Class 3) are enrolled and usable. */
    Strong,

    /** Only weak biometrics (Class 2, e.g. some 2D face sensors) are usable. */
    WeakOnly,

    /** No biometrics, but the device has a PIN, pattern or password. */
    DeviceCredentialOnly,

    /** No lock screen at all. */
    None;

    /** The connection vault may be enabled for this capability (D2, D3). */
    val allowsConnectionVault: Boolean get() = this != None

    /**
     * Only strong biometrics may protect a saved host administrator password (D1).
     * The elevation vault is refused for [WeakOnly] and [DeviceCredentialOnly].
     */
    val allowsElevationVault: Boolean get() = this == Strong

    /** Device credential only becomes available after an explicit opt-in in settings (D3). */
    val requiresExplicitOptIn: Boolean get() = this == DeviceCredentialOnly

    /** True when the vault is protected by something weaker than a strong biometric. */
    val isReducedStrength: Boolean get() = this == WeakOnly || this == DeviceCredentialOnly
}

/**
 * How a vault key is released. The mode follows from the capability and the vault kind and decides
 * whether unlocking needs a [androidx.biometric.BiometricPrompt.CryptoObject] or a time window.
 */
enum class VaultUnlockMode {
    /** Per-use authorization through a CryptoObject-bound Cipher. */
    PerUseStrongBiometric,

    /**
     * Device-unlock based window. Required for weak biometrics and for device credentials, because
     * neither can be bound to a CryptoObject per-use.
     */
    DeviceUnlockWindow,
}

/** Resolves the unlock mode for one vault kind, or `null` when that vault must stay disabled. */
fun unlockModeFor(kind: VaultKind, capability: BiometricCapability, deviceUnlockWindowEnabled: Boolean): VaultUnlockMode? =
    when {
        capability == BiometricCapability.None -> null
        kind == VaultKind.Elevation ->
            if (capability.allowsElevationVault) VaultUnlockMode.PerUseStrongBiometric else null
        capability == BiometricCapability.Strong -> VaultUnlockMode.PerUseStrongBiometric
        deviceUnlockWindowEnabled -> VaultUnlockMode.DeviceUnlockWindow
        else -> null
    }

/** Pure mapping from authenticator availability to the public capability set. */
fun capabilityFor(strongAvailable: Boolean, weakAvailable: Boolean, deviceCredentialAvailable: Boolean): BiometricCapability =
    when {
        strongAvailable -> BiometricCapability.Strong
        weakAvailable -> BiometricCapability.WeakOnly
        deviceCredentialAvailable -> BiometricCapability.DeviceCredentialOnly
        else -> BiometricCapability.None
    }

/** Probes the platform. Kept behind an interface so screens and tests never touch BiometricManager. */
interface BiometricCapabilityDetector {
    fun detect(): BiometricCapability
}

/** [BiometricCapabilityDetector] backed by `androidx.biometric`. */
class AndroidBiometricCapabilityDetector(private val context: Context) : BiometricCapabilityDetector {
    override fun detect(): BiometricCapability {
        val manager = BiometricManager.from(context)
        val strong = manager.canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG) == BiometricManager.BIOMETRIC_SUCCESS
        val weak = manager.canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_WEAK) == BiometricManager.BIOMETRIC_SUCCESS
        val deviceCredential = manager.canAuthenticate(BiometricManager.Authenticators.DEVICE_CREDENTIAL) == BiometricManager.BIOMETRIC_SUCCESS
        return capabilityFor(strong, weak, deviceCredential)
    }
}
