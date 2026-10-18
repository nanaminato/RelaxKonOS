package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.data.ReminderKind
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultRecord
import app.relaxkonos.mobile.security.DebugCredentialRecord
import app.relaxkonos.mobile.security.BiometricCapability
import app.relaxkonos.mobile.security.VaultUnlockMode

internal interface AccountSecurityActions {
    val fingerprintEnabled: Boolean
    val capability: BiometricCapability
    val connectionRecords: List<VaultRecord>
    val elevationRecords: List<VaultRecord>
    val debugRecord: DebugCredentialRecord?
    val connectionMode: VaultUnlockMode?
    val elevationMode: VaultUnlockMode?
    val silenced: List<ReminderKind>
    fun isCurrentSession(expected: SessionState.Active?): Boolean
    suspend fun resolvePlatforms()
    fun enableFingerprint()
    fun disableFingerprint()
    fun clearCredentials()
    fun delete(record: VaultRecord)
    fun deleteDebugCredential(record: DebugCredentialRecord)
    fun clearUsageMemory()
    fun restoreReminder(kind: ReminderKind)
}

/** Coordinates security changes across both vaults, debug storage and Keystore. */
internal class AccountSecurityController(private val container: AppContainer) : AccountSecurityActions {
    override val fingerprintEnabled get() = container.appearance.fingerprintEnabled
    override val capability get() = container.biometricCapability()
    override val connectionRecords get() = container.vault.records(VaultKind.Connection)
    override val elevationRecords get() = container.vault.records(VaultKind.Elevation)
    override val debugRecord get() = container.debugCredentials?.record()
    override val connectionMode get() = container.unlockMode(VaultKind.Connection)
    override val elevationMode get() = container.unlockMode(VaultKind.Elevation)
    override val silenced get() = container.notices.silenced()

    override fun isCurrentSession(expected: SessionState.Active?) = container.activeSession === expected

    override suspend fun resolvePlatforms() {
        container.hostOperatingSystems.resolve(
            connectionRecords.map { it.serviceId } + elevationRecords.map { it.serviceId } +
                listOfNotNull(debugRecord?.serviceId), container.activeSession,
        )
    }

    override fun enableFingerprint() = container.appearance.setFingerprintEnabled(true)

    override fun disableFingerprint() {
        container.appearance.setFingerprintEnabled(false)
        clearCredentials()
        container.keyManager.deleteKey(VaultKind.Connection)
        container.keyManager.deleteKey(VaultKind.Elevation)
    }

    override fun clearCredentials() {
        container.vault.clear(VaultKind.Connection)
        container.vault.clear(VaultKind.Elevation)
        clearDebugCredential()
    }

    override fun delete(record: VaultRecord) = container.vault.delete(record)
    override fun deleteDebugCredential(record: DebugCredentialRecord) { container.debugCredentials?.delete(record.serviceId, record.identifier) }
    private fun clearDebugCredential() { container.debugCredentials?.clear() }
    override fun clearUsageMemory() = container.usageMemory.clear(container.activeSession)
    override fun restoreReminder(kind: ReminderKind) = container.notices.setSilenced(kind, false)
}
