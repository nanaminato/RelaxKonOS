package app.relaxkonos.mobile.ui.more

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
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.theme.Spacing

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
    val model: DiagnosticsViewModel = viewModel()
    DiagnosticsContent(model.diagnostics, onBack, modifier)
}

@Composable
internal fun DiagnosticsContent(
    viewModel: DiagnosticsController,
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {

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
                EmptyHint(stringResource(if (viewModel.running) R.string.common_loading else R.string.diagnostics_not_run))
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
                stringResource(R.string.diagnostics_vault_summary, viewModel.connectionCredentialCount, viewModel.elevationCredentialCount),
                style = MaterialTheme.typography.bodyMedium,
            )
        }
    }

    viewModel.export?.let { report ->
        AlertDialog(
            onDismissRequest = { viewModel.dismissExport() },
            title = { Text(stringResource(R.string.diagnostics_export_title)) },
            text = {
                androidx.compose.foundation.text.selection.SelectionContainer {
                    Text(report, modifier = Modifier.verticalScroll(rememberScrollState()),
                        style = MaterialTheme.typography.bodySmall)
                }
            },
            confirmButton = {
                TextButton(onClick = { viewModel.dismissExport() }) { Text(stringResource(R.string.common_close)) }
            },
        )
    }
}
