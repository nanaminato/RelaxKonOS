package app.relaxkonos.mobile.ui.more

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.theme.Spacing
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
            collected += if (session == null) text(R.string.diagnostics_no_session) else text(R.string.diagnostics_session_ok, session.serverUrl)
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
            appendLine("serverUrl=" + (session?.serverUrl ?: "-"))
            appendLine("serverPlatform=" + (session?.serverPlatform ?: "-"))
            appendLine("workspace=" + (session?.workspaceName ?: "-"))
            appendLine("capabilities=" + container.capabilities.sorted().joinToString(","))
            appendLine("accessTokenHeld=" + (container.session.accessToken != null))
            appendLine("connectionVaultRecords=" + container.vault.records(VaultKind.Connection).size)
            appendLine("elevationVaultRecords=" + container.vault.records(VaultKind.Elevation).size)
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

/**
 * Diagnostics.
 *
 * The page exists to answer "is this connection actually healthy", so it reports transport failures as
 * transport failures — a green result that hides a timeout would defeat the purpose.
 */
@Composable
fun DiagnosticsScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val viewModel: DiagnosticsViewModel = viewModel()
    val container = appContainer()

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.diagnostics_title),
            onBack = onBack,
        )

        SectionCard(
            title = stringResource(R.string.diagnostics_self_check),
            trailing = {
                Button(onClick = { viewModel.run() }, enabled = !viewModel.running) {
                    Text(stringResource(R.string.diagnostics_run))
                }
            },
        ) {
            if (viewModel.lines.isEmpty()) {
                EmptyHint(stringResource(R.string.diagnostics_not_run))
            } else {
                viewModel.lines.forEach { line ->
                    Text(line, style = MaterialTheme.typography.bodyMedium)
                }
            }
        }

        SectionCard(stringResource(R.string.diagnostics_export_title)) {
            Text(
                stringResource(R.string.diagnostics_export_note),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Button(onClick = { viewModel.buildExport() }, enabled = !viewModel.running) {
                Text(stringResource(R.string.diagnostics_export))
            }
        }

        SectionCard(stringResource(R.string.account_security_device_title)) {
            Text(
                stringResource(R.string.diagnostics_vault_summary, container.vault.records(VaultKind.Connection).size, container.vault.records(VaultKind.Elevation).size),
                style = MaterialTheme.typography.bodyMedium,
            )
        }
    }

    viewModel.export?.let { report ->
        AlertDialog(
            onDismissRequest = { viewModel.dismissExport() },
            title = { Text(stringResource(R.string.diagnostics_export_title)) },
            text = { Text(report, style = MaterialTheme.typography.bodySmall) },
            confirmButton = {
                TextButton(onClick = { viewModel.dismissExport() }) { Text(stringResource(R.string.common_close)) }
            },
        )
    }
}
