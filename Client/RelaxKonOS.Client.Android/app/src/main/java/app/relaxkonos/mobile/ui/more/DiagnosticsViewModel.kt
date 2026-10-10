package app.relaxkonos.mobile.ui.more

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.BuildConfig
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.security.VaultKind
import kotlinx.coroutines.CancellationException

/** Android adapters supply localized strings and redacted device facts to the diagnostics state. */
class DiagnosticsViewModel(application: Application) : AndroidViewModel(application) {
    private val app = getApplication<RelaxKonApplication>()
    private val container = app.container
    internal val diagnostics = DiagnosticsController(container.session, container.system,
        readFiles = { owner ->
            fun guard() { if (container.session.state.value !== owner) throw CancellationException("Diagnostics owner changed") }
            val result = container.session.authenticated { url, token ->
                guard()
                container.gateway.listDirectory(url, token, "").also { guard() }
            }
            guard()
            when (result) {
                is ApiResult.Success -> ApiResult.Success(result.value.entries.size)
                is ApiResult.Problem -> result
                is ApiResult.Transport -> result
            }
        }, deviceFacts = {
            DiagnosticsDeviceFacts(BuildConfig.VERSION_NAME,
                container.vault.records(VaultKind.Connection).size, container.vault.records(VaultKind.Elevation).size,
                container.debugCredentials?.hasRecord() ?: false, container.biometricCapability().name,
                container.appearance.fingerprintEnabled)
        }, localized = { id, args -> app.getString(id, *args) }, scope = viewModelScope)
}
