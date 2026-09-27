package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.ServerCenterCoordinator
import app.relaxkonos.mobile.servercenter.ServerCenterSshVerification
import app.relaxkonos.mobile.servercenter.ServerHostTarget
import app.relaxkonos.mobile.servercenter.ServerHostTargetRules
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/**
 * Stateful server-centre workflow. Compose components only render [ServerCenterUiState] and forward
 * user events here; SSH credentials never become navigation arguments or persisted UI state.
 */
class ServerCenterViewModel(application: Application) : AndroidViewModel(application) {
    private val coordinator: ServerCenterCoordinator =
        getApplication<RelaxKonApplication>().container.serverCenter

    private val mutableState = MutableStateFlow(ServerCenterUiState(hosts = coordinator.hosts()))
    val state = mutableState.asStateFlow()

    fun updateAddHost(value: String) = update { copy(addHost = value, inputError = false) }

    fun updateAddPort(value: String) = update { copy(addPort = value, inputError = false) }

    fun updateAddUser(value: String) = update { copy(addUser = value, inputError = false) }

    fun updateAddName(value: String) = update { copy(addName = value) }

    fun updateAddPassword(value: String) = update { copy(addPassword = value, inputError = false) }

    fun updateManagePassword(value: String) = update {
        copy(managePassword = value, pendingPassword = "", verification = null)
    }

    fun selectHost(hostId: String) = update {
        copy(selectedHostId = hostId, managePassword = "", pendingPassword = "", verification = null)
    }

    fun addAndVerify() {
        val current = mutableState.value
        val port = current.addPort.toIntOrNull()
        if (port == null || !ServerHostTargetRules.isValidEndpoint(current.addHost, port, current.addUser) ||
            current.addPassword.isEmpty()
        ) {
            update { copy(inputError = true) }
            return
        }

        val target = coordinator.addHost(current.addHost, port, current.addUser, current.addName.ifBlank { null })
        val password = current.addPassword.toCharArray()
        update {
            copy(
                hosts = coordinator.hosts(),
                selectedHostId = target.hostId,
                addPassword = "",
                pendingPassword = current.addPassword,
                verification = null,
                isVerifying = true,
            )
        }
        verify(target, password, clearAddFormOnSuccess = true)
    }

    fun verifySelectedHost() {
        val target = selectedTarget() ?: return
        val password = mutableState.value.managePassword
        if (password.isEmpty()) return
        update { copy(verification = null, isVerifying = true) }
        verify(target, password.toCharArray(), clearAddFormOnSuccess = false)
    }

    fun trustAndVerify() {
        val target = selectedTarget() ?: return
        val verification = mutableState.value.verification as? ServerCenterSshVerification.NeedsTrust ?: return
        coordinator.trustHostKey(target, verification.observation)
        val current = mutableState.value
        val password = current.pendingPassword.ifEmpty { current.managePassword }
        if (password.isEmpty()) {
            update { copy(verification = null) }
            return
        }
        update { copy(isVerifying = true) }
        verify(target, password.toCharArray(), clearAddFormOnSuccess = current.pendingPassword.isNotEmpty())
    }

    fun requestDelete() = update { copy(deleteRequested = selectedTarget() != null) }

    fun dismissDelete() = update { copy(deleteRequested = false) }

    fun removeSelectedHost() {
        val target = selectedTarget() ?: return
        coordinator.removeHost(target.hostId)
        update {
            copy(
                hosts = coordinator.hosts(),
                selectedHostId = null,
                managePassword = "",
                pendingPassword = "",
                verification = null,
                deleteRequested = false,
            )
        }
    }

    private fun verify(target: ServerHostTarget, password: CharArray, clearAddFormOnSuccess: Boolean) {
        viewModelScope.launch {
            val result = try {
                coordinator.verifySsh(target.hostId, SshCredential(SshCredentialKind.Password, password, null))
            } finally {
                // The coordinator clears the credential, and this is harmless if a transport failed
                // before taking ownership. Keep the UI's String separate from this request buffer.
                password.fill('\u0000')
            }
            update {
                val succeeded = result is ServerCenterSshVerification.Trusted
                copy(
                    verification = result,
                    isVerifying = false,
                    pendingPassword = if (succeeded) "" else pendingPassword,
                    addHost = if (succeeded && clearAddFormOnSuccess) "" else addHost,
                    addPort = if (succeeded && clearAddFormOnSuccess) "22" else addPort,
                    addUser = if (succeeded && clearAddFormOnSuccess) "" else addUser,
                    addName = if (succeeded && clearAddFormOnSuccess) "" else addName,
                )
            }
        }
    }

    private fun selectedTarget(): ServerHostTarget? {
        val hostId = mutableState.value.selectedHostId ?: return null
        return mutableState.value.hosts.firstOrNull { it.hostId == hostId }
    }

    private inline fun update(transform: ServerCenterUiState.() -> ServerCenterUiState) {
        mutableState.update(transform)
    }
}

data class ServerCenterUiState(
    val hosts: List<ServerHostTarget>,
    val addHost: String = "",
    val addPort: String = "22",
    val addUser: String = "",
    val addName: String = "",
    val addPassword: String = "",
    val inputError: Boolean = false,
    val selectedHostId: String? = null,
    val managePassword: String = "",
    val pendingPassword: String = "",
    val verification: ServerCenterSshVerification? = null,
    val isVerifying: Boolean = false,
    val deleteRequested: Boolean = false,
)
