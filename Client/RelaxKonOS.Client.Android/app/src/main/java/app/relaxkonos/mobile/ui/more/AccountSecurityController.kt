package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.data.ReminderKind
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultRecord

/** Coordinates security changes across both vaults, debug storage and Keystore. */
internal class AccountSecurityController(private val container: AppContainer) {
    val capability get() = container.biometricCapability()
    val connectionRecords get() = container.vault.records(VaultKind.Connection)
    val elevationRecords get() = container.vault.records(VaultKind.Elevation)
    val debugRecord get() = container.debugCredentials?.record()
    val connectionMode get() = container.unlockMode(VaultKind.Connection)
    val elevationMode get() = container.unlockMode(VaultKind.Elevation)
    val silenced get() = container.notices.silenced()

    suspend fun resolvePlatforms() {
        container.hostOperatingSystems.resolve(
            connectionRecords.map { it.serviceId } + elevationRecords.map { it.serviceId } +
                listOfNotNull(debugRecord?.serviceId), container.activeSession,
        )
    }

    fun enableFingerprint() = container.appearance.setFingerprintEnabled(true)

    fun disableFingerprint() {
        container.appearance.setFingerprintEnabled(false)
        clearCredentials()
        container.keyManager.deleteKey(VaultKind.Connection)
        container.keyManager.deleteKey(VaultKind.Elevation)
    }

    fun clearCredentials() {
        container.vault.clear(VaultKind.Connection)
        container.vault.clear(VaultKind.Elevation)
        clearDebugCredential()
    }

    fun delete(record: VaultRecord) = container.vault.delete(record)
    fun clearDebugCredential() { container.debugCredentials?.clear() }
    fun clearUsageMemory() = container.usageMemory.clear(container.activeSession)
    fun restoreReminder(kind: ReminderKind) = container.notices.setSilenced(kind, false)
}
