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

/** One form, one transient password and no saved record until the first SSH handshake succeeds. */
class ServerCenterViewModel(application: Application) : AndroidViewModel(application) {
    private val coordinator: ServerCenterCoordinator =
        getApplication<RelaxKonApplication>().container.serverCenter

    private val mutableState = MutableStateFlow(ServerCenterUiState(hosts = coordinator.hosts()))
    val state = mutableState.asStateFlow()

    fun updateHost(value: String) = update { copy(host = value, inputError = false, verification = null) }
    fun updatePort(value: String) = update { copy(port = value, inputError = false, verification = null) }
    fun updateUser(value: String) = update { copy(user = value, inputError = false, verification = null) }
    fun updateName(value: String) = update { copy(name = value, verification = null) }
    fun updatePassword(value: String) = update { copy(password = value, inputError = false, verification = null) }

    fun addAndVerify() {
        val state = mutableState.value
        val port = state.port.toIntOrNull()
        if (port == null || !ServerHostTargetRules.isValidEndpoint(state.host, port, state.user) || state.password.isEmpty()) {
            update { copy(inputError = true) }
            return
        }
        val target = ServerHostTargetRules.create(state.host, port, state.user, state.name.ifBlank { null }, System.currentTimeMillis())
        verify(target, ServerCenterFormMode.Add, clearFormOnSuccess = true)
    }

    /** Opens the same form with the stored endpoint, rather than adding a second password field. */
    fun manage(hostId: String) {
        val target = mutableState.value.hosts.firstOrNull { it.hostId == hostId } ?: return
        update {
            copy(
                formMode = ServerCenterFormMode.Manage,
                selectedHostId = target.hostId,
                host = target.sshHost,
                port = target.sshPort.toString(),
                user = target.sshUserName,
                name = target.displayName,
                password = "",
                pendingTarget = null,
                pendingPassword = "",
                verification = null,
                inputError = false,
            )
        }
    }

    /** A successful re-check goes straight to the SSH workspace. */
    fun verifyAndOpen() {
        val target = selectedTarget() ?: return
        if (mutableState.value.password.isEmpty()) {
            update { copy(inputError = true) }
            return
        }
        verify(target, ServerCenterFormMode.Manage, clearFormOnSuccess = false)
    }

    fun trustAndVerify() {
        val state = mutableState.value
        val target = state.pendingTarget ?: return
        val confirmation = state.verification as? ServerCenterSshVerification.NeedsTrust ?: return
        val password = state.pendingPassword
        if (password.isEmpty()) {
            update { copy(verification = null, pendingTarget = null) }
            return
        }
        coordinator.trustHostKey(target, confirmation.observation)
        verify(target, state.formMode, clearFormOnSuccess = state.formMode == ServerCenterFormMode.Add, passwordOverride = password)
    }

    fun dismissHostKeyTrust() = update { copy(verification = null, pendingTarget = null, pendingPassword = "") }
    fun requestDelete() = update { copy(deleteRequested = selectedTarget() != null) }
    fun dismissDelete() = update { copy(deleteRequested = false) }

    fun removeSelectedHost() {
        val target = selectedTarget() ?: return
        coordinator.removeHost(target.hostId)
        update {
            copy(
                hosts = coordinator.hosts(), formMode = ServerCenterFormMode.Add, selectedHostId = null,
                host = "", port = "22", user = "", name = "", password = "",
                pendingTarget = null, pendingPassword = "", verification = null, deleteRequested = false,
            )
        }
    }

    private fun verify(
        target: ServerHostTarget,
        mode: ServerCenterFormMode,
        clearFormOnSuccess: Boolean,
        passwordOverride: String? = null,
    ) {
        val password = passwordOverride ?: mutableState.value.password
        if (password.isEmpty()) return
        update {
            copy(
                formMode = mode, password = "", pendingPassword = password, pendingTarget = target,
                verification = null, isVerifying = true,
            )
        }
        viewModelScope.launch {
            val secret = password.toCharArray()
            val result = try {
                coordinator.verifySsh(target, SshCredential(SshCredentialKind.Password, secret, null))
            } finally {
                secret.fill('\u0000')
            }
            val succeeded = result is ServerCenterSshVerification.Trusted
            if (succeeded) coordinator.saveHost(
                target.copy(
                    sshVerifiedAtEpochMillis = System.currentTimeMillis(),
                    lastUsedAtEpochMillis = System.currentTimeMillis(),
                ),
            )
            update {
                copy(
                    hosts = if (succeeded) coordinator.hosts() else hosts,
                    selectedHostId = if (succeeded && clearFormOnSuccess) target.hostId else selectedHostId,
                    pendingTarget = null, pendingPassword = "", verification = result, isVerifying = false,
                    formMode = if (succeeded && clearFormOnSuccess) ServerCenterFormMode.Add else formMode,
                    host = if (succeeded && clearFormOnSuccess) "" else host,
                    port = if (succeeded && clearFormOnSuccess) "22" else port,
                    user = if (succeeded && clearFormOnSuccess) "" else user,
                    name = if (succeeded && clearFormOnSuccess) "" else name,
                )
            }
            if (succeeded && mode == ServerCenterFormMode.Manage) {
                val workspaceSecret = password.toCharArray()
                try {
                    coordinator.openSshFiles(target.hostId, workspaceSecret)
                } finally {
                    workspaceSecret.fill('\u0000')
                }
            }
        }
    }

    private fun selectedTarget(): ServerHostTarget? =
        mutableState.value.selectedHostId?.let { id -> mutableState.value.hosts.firstOrNull { it.hostId == id } }

    private inline fun update(transform: ServerCenterUiState.() -> ServerCenterUiState) = mutableState.update(transform)
}

enum class ServerCenterFormMode { Add, Manage }

data class ServerCenterUiState(
    val hosts: List<ServerHostTarget>,
    val formMode: ServerCenterFormMode = ServerCenterFormMode.Add,
    val host: String = "",
    val port: String = "22",
    val user: String = "",
    val name: String = "",
    val password: String = "",
    val inputError: Boolean = false,
    val selectedHostId: String? = null,
    val pendingTarget: ServerHostTarget? = null,
    val pendingPassword: String = "",
    val verification: ServerCenterSshVerification? = null,
    val isVerifying: Boolean = false,
    val deleteRequested: Boolean = false,
)
