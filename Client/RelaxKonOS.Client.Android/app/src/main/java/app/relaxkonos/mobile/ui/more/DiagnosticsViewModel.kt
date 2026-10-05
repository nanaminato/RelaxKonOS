package app.relaxkonos.mobile.ui.more

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import android.app.Application
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.security.VaultKind
import kotlinx.coroutines.launch
/**
 * Diagnostics state.
 *
 * The self-check probes the live session and reports what actually happened; on a transport failure it
 * says so instead of reporting a green result. The exported text is assembled from non-secret values
 * only — no token, no password, no ciphertext — which is what makes it safe to hand to someone else
 * (design §7, secret scan).
 */
class DiagnosticsViewModel(application: Application) : AndroidViewModel(application) {
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container
    val connectionCredentialCount get() = container.vault.records(VaultKind.Connection).size
    val elevationCredentialCount get() = container.vault.records(VaultKind.Elevation).size

    var lines by mutableStateOf<List<String>>(emptyList())
        private set

    var running by mutableStateOf(false)
        private set

    var export by mutableStateOf<String?>(null)
        private set

    fun run() {
        if (running) {
            return
        }
        running = true
        lines = emptyList()
        viewModelScope.launch {
            val collected = mutableListOf<String>()
            val session = container.activeSession
            collected += if (session == null) text(R.string.diagnostics_no_session) else text(R.string.diagnostics_session_ok, session.serviceId)
            collected += if (container.session.accessToken != null) {
                text(R.string.diagnostics_token_held)
            } else {
                text(R.string.diagnostics_token_absent)
            }
            if (session != null) {
                collected += text(R.string.diagnostics_capabilities, session.capabilities.size)
            }

            if (container.capabilities.contains(ServerCapabilities.METRICS)) {
                collected += when (val result = container.system.performance()) {
                    is ApiResult.Success -> text(R.string.diagnostics_metrics_ok)
                    is ApiResult.Problem -> text(R.string.diagnostics_metrics_problem)
                    is ApiResult.Transport -> text(R.string.diagnostics_metrics_transport)
                }
            }
            if (container.capabilities.contains(ServerCapabilities.FILES)) {
                collected += when (val result = container.files.list("", container.elevationAnswers)) {
                    is ApiResult.Success -> text(R.string.diagnostics_files_ok, result.value.entries.size)
                    is ApiResult.Problem -> text(R.string.diagnostics_files_problem)
                    is ApiResult.Transport -> text(R.string.diagnostics_files_transport)
                }
            }
            lines = collected
            running = false
        }
    }

    /** Builds the redacted report. Called from a user action, so it never runs in the background. */
    fun buildExport() {
        val session = container.activeSession
        val report = buildString {
            appendLine("RelaxKonOS Android diagnostics")
            appendLine("clientVersion=" + appVersion())
            appendLine("serviceId=" + (session?.serviceId ?: "-"))
            appendLine("effectiveBaseUrl=" + (session?.effectiveBaseUrl ?: "-"))
            appendLine("serverPlatform=" + (session?.serverPlatform ?: "-"))
            appendLine("workspace=" + (session?.workspaceName ?: "-"))
            appendLine("capabilities=" + container.capabilities.sorted().joinToString(","))
            appendLine("accessTokenHeld=" + (container.session.accessToken != null))
            appendLine("connectionVaultRecords=" + container.vault.records(VaultKind.Connection).size)
            appendLine("elevationVaultRecords=" + container.vault.records(VaultKind.Elevation).size)
            // Debug-only, and a boolean rather than a count: there is never more than one record. A
            // report that left out a plaintext password on disk would be the one line nobody could act on.
            appendLine("debugCredentialRecord=" + (container.debugCredentials?.hasRecord() ?: false))
            appendLine("biometricCapability=" + container.biometricCapability().name)
            appendLine("fingerprintEnabled=" + container.appearance.fingerprintEnabled)
            appendLine("--- self check ---")
            lines.forEach { appendLine(it) }
        }
        export = report
    }

    fun dismissExport() {
        export = null
    }

    private fun appVersion(): String = getApplication<RelaxKonApplication>().let {
        app.relaxkonos.mobile.BuildConfig.VERSION_NAME
    }

    private fun text(resId: Int, vararg args: Any): String =
        getApplication<RelaxKonApplication>().getString(resId, *args)
}
