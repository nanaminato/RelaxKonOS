package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.core.auth.credentialState
import app.relaxkonos.mobile.core.auth.credentialStatus
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.model.SavedLogin

/** Keeps login records and their stored credentials consistent after a confirmed action. */
internal class ConnectionsController(private val container: AppContainer) {
    val logins get() = container.profiles.all()

    suspend fun resolvePlatforms() = container.hostOperatingSystems.resolve(
        logins.map { it.serviceId }, container.activeSession,
    )

    fun status(login: SavedLogin) = container.unlockMode(VaultKind.Connection).let { mode ->
        val record = container.vault.record(VaultKind.Connection, login.serviceId, login.identifier)
        credentialStatus(credentialState(record, mode), mode)
    }

    fun forgetPassword(login: SavedLogin) {
        removeCredential(login)
        container.profiles.setHasSavedCredential(login.serviceId, login.identifier, false)
    }

    fun delete(login: SavedLogin) {
        removeCredential(login)
        container.profiles.remove(login.serviceId, login.identifier)
    }

    private fun removeCredential(login: SavedLogin) {
        container.vault.delete(VaultKind.Connection, login.serviceId, login.identifier)
        container.forgetDebugCredential(login.serviceId, login.identifier)
    }
}
