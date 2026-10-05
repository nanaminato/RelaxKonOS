package app.relaxkonos.mobile.ui.servercenter

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.servercenter.SshCredential
import app.relaxkonos.mobile.servercenter.SshSystemProbe
import app.relaxkonos.mobile.servercenter.SshSystemSnapshot
import app.relaxkonos.mobile.servercenter.SshDiagnostics
import app.relaxkonos.mobile.servercenter.SshCredentialKind
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
/** Fixed, read-only Windows and Linux host inspection.  The UI never accepts a command string from the user. */
class SshSystemViewModel(application: Application) : AndroidViewModel(application) {
    private val container = getApplication<RelaxKonApplication>().container
    private val mutableState = MutableStateFlow(SshSystemUiState())
    val state = mutableState.asStateFlow()

    fun refresh(hostId: String) {
        if (mutableState.value.loading) return
        val secret = container.serverCenter.workspacePasswordCopy()
        if (secret == null) {
            mutableState.update { it.copy(problem = true, loading = false) }
            return
        }
        mutableState.update { it.copy(loading = true, problem = false) }
        viewModelScope.launch {
            try {
                val snapshot = container.serverCenterConnections.connect(
                    hostId,
                    SshCredential(SshCredentialKind.Password, secret, null),
                    System.currentTimeMillis(),
                ).use { session ->
                    SshSystemProbe.read(session.sshTransport)
                }
                mutableState.update { it.copy(snapshot = snapshot ?: it.snapshot, loading = false, problem = snapshot == null) }
            } catch (error: Exception) {
                SshDiagnostics.failure("system.failed", error)
                mutableState.update { it.copy(loading = false, problem = true) }
            } finally {
                secret.fill('\u0000')
            }
        }
    }

}

data class SshSystemUiState(
    val loading: Boolean = false,
    val snapshot: SshSystemSnapshot? = null,
    val problem: Boolean = false,
)
