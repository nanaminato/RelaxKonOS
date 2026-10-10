package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.data.DeploymentBrowserState
import app.relaxkonos.mobile.ui.common.RefreshProgressIndicator
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.theme.Spacing

@Composable
internal fun DeploymentLogsContent(state: DeploymentBrowserState, onLoad: () -> Unit, onLoadMore: () -> Unit, onRetry: () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        Text(stringResource(R.string.deployments_logs), style = MaterialTheme.typography.titleMedium)
        RefreshProgressIndicator(visible = state.logsLoading)
        when (val logs = state.logs) {
            is ApiResult.Success -> SectionCard(title = stringResource(if (logs.value.truncated) R.string.deployments_logs_truncated else R.string.deployments_logs_recent)) {
                if (logs.value.lines.isEmpty()) Text(stringResource(R.string.deployments_logs_empty))
                logs.value.lines.forEach { Text(it, style = MaterialTheme.typography.bodySmall) }
                state.loadedLogTail?.takeIf { logs.value.truncated && it < 1_000 }?.let { tail ->
                    TextButton(onClick = onLoadMore, enabled = !state.logsLoading) {
                        Text(stringResource(R.string.deployments_logs_load_more, (tail * 5).coerceAtMost(1_000)))
                    }
                }
            }
            null -> SectionCard(title = stringResource(R.string.deployments_logs_recent)) {
                Text(stringResource(R.string.deployments_logs_on_demand), style = MaterialTheme.typography.bodySmall)
                TextButton(onClick = onLoad, enabled = !state.logsLoading) {
                    Text(stringResource(R.string.deployments_logs_load_initial, 20))
                }
            }
            else -> SectionCard(title = stringResource(R.string.deployments_logs_recent)) {
                Text(if (logs is ApiResult.Transport) stringResource(R.string.deployments_logs_read_failed) else logs.deploymentFailure().text(),
                    color = MaterialTheme.colorScheme.error)
                TextButton(onClick = onRetry, enabled = !state.logsLoading) {
                    Text(stringResource(R.string.deployments_logs_retry))
                }
            }
        }
    }
}
