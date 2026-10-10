package app.relaxkonos.mobile.ui.more

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.data.SystemRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

internal data class DiagnosticsDeviceFacts(val clientVersion: String, val connectionRecords: Int,
    val elevationRecords: Int, val debugCredentialRecord: Boolean, val biometricCapability: String,
    val fingerprintEnabled: Boolean)

/** Session diagnostics contain read outcomes and non-secret device facts only. */
internal class DiagnosticsController(private val session: AuthSession, private val system: SystemRepository,
    private val readFiles: suspend (SessionState.Active) -> ApiResult<Int>, private val deviceFacts: () -> DiagnosticsDeviceFacts,
    private val localized: (Int, Array<out Any>) -> String, private val scope: CoroutineScope) {
    val connectionCredentialCount get() = deviceFacts().connectionRecords
    val elevationCredentialCount get() = deviceFacts().elevationRecords
    var lines by mutableStateOf<List<String>>(emptyList())
        private set
    var running by mutableStateOf(false)
        private set
    var export by mutableStateOf<String?>(null)
        private set
    private var owner = session.state.value as? SessionState.Active
    private var generation = 0
    private var job: Job? = null
    init {
        scope.launch { session.state.collect { value ->
            val active = value as? SessionState.Active
            if (owner !== active) resetOwner(active)
        } }
    }
    private fun resetOwner(active: SessionState.Active?) {
        generation++; job?.cancel(); job = null; owner = active
        lines = emptyList(); export = null; running = false
    }
    private suspend fun <T> read(check: suspend () -> ApiResult<T>): ApiResult<T> =
        try { check() }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { ApiResult.Transport(null) }
    private fun text(id: Int, vararg args: Any) = localized(id, args)
    fun run() {
        val active = session.state.value as? SessionState.Active
        if (owner !== active) resetOwner(active)
        if (running) return
        running = true; lines = emptyList(); export = null
        val version = ++generation
        fun current() = version == generation && (session.state.value as? SessionState.Active) === active
        fun guard() { if (!current()) throw CancellationException("Diagnostics owner changed") }
        job = scope.launch {
          try {
            val collected = mutableListOf<String>()
            guard()
            collected += if (active == null) text(R.string.diagnostics_no_session) else text(R.string.diagnostics_session_ok, active.serviceId)
            collected += text(if (session.accessToken != null) R.string.diagnostics_token_held else R.string.diagnostics_token_absent)
            if (active != null) collected += text(R.string.diagnostics_capabilities, active.capabilities.size)
            if (active?.capabilities?.contains(ServerCapabilities.METRICS) == true) {
                val result = read { system.performance(active) }; guard()
                collected += when (result) {
                    is ApiResult.Success -> text(R.string.diagnostics_metrics_ok)
                    is ApiResult.Problem -> text(R.string.diagnostics_metrics_problem)
                    is ApiResult.Transport -> text(R.string.diagnostics_metrics_transport)
                }
            }
            if (active?.capabilities?.contains(ServerCapabilities.FILES) == true) {
                guard()
                val result = read { readFiles(active) }; guard()
                collected += when (result) {
                    is ApiResult.Success -> text(R.string.diagnostics_files_ok, result.value)
                    is ApiResult.Problem -> text(R.string.diagnostics_files_problem)
                    is ApiResult.Transport -> text(R.string.diagnostics_files_transport)
                }
            }
            guard(); lines = collected
          } finally { if (current()) running = false }
        }
    }
    fun buildExport() {
        val active = session.state.value as? SessionState.Active
        if (owner !== active) resetOwner(active)
        if (running) return
        val facts = deviceFacts()
        val report = buildString {
            appendLine("RelaxKonOS Android diagnostics")
            appendLine("clientVersion=" + facts.clientVersion)
            appendLine("serviceId=" + (active?.serviceId ?: "-"))
            appendLine("effectiveBaseUrl=" + (active?.effectiveBaseUrl ?: "-"))
            appendLine("serverPlatform=" + (active?.serverPlatform ?: "-"))
            appendLine("workspace=" + (active?.workspaceName ?: "-"))
            appendLine("capabilities=" + active?.capabilities.orEmpty().sorted().joinToString(","))
            appendLine("accessTokenHeld=" + (session.accessToken != null))
            appendLine("connectionVaultRecords=" + facts.connectionRecords)
            appendLine("elevationVaultRecords=" + facts.elevationRecords)
            appendLine("debugCredentialRecord=" + facts.debugCredentialRecord)
            appendLine("biometricCapability=" + facts.biometricCapability)
            appendLine("fingerprintEnabled=" + facts.fingerprintEnabled)
            appendLine("--- self check ---")
            lines.forEach { appendLine(it) }
        }
        if ((session.state.value as? SessionState.Active) === active) export = report
    }
    fun dismissExport() { export = null }
}
