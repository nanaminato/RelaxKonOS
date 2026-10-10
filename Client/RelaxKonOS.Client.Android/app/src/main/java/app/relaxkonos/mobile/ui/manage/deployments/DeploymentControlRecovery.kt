package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.data.DeploymentBrowserState
import app.relaxkonos.mobile.data.DeploymentControlKind
import app.relaxkonos.mobile.ui.common.SectionCard

@Composable
internal fun DeploymentControlRecovery(state: DeploymentBrowserState, onRetry: () -> Unit) {
    if (!state.controlBlocked) return
    Column(Modifier.heightIn(max = 240.dp).verticalScroll(rememberScrollState())) {
      SectionCard(title = stringResource(R.string.deployments_control_recovery)) {
        if (state.controlStorageUnavailable) Text(stringResource(R.string.deployments_control_storage_failed), color = MaterialTheme.colorScheme.error)
        else state.pendingControl?.let {
            Text(controlActionLabel(it.kind))
            if (it.argument.isNotEmpty()) Text(it.argument)
            Text(stringResource(R.string.deployments_control_unknown, it.applicationId))
            TextButton(onClick = onRetry, enabled = !state.submitting) { Text(stringResource(R.string.deployments_control_retry)) }
        }
      }
    }
}

@Composable
internal fun controlActionLabel(kind: DeploymentControlKind): String = stringResource(when (kind) {
    DeploymentControlKind.Rollback -> R.string.deployment_rollback
    DeploymentControlKind.Delete -> R.string.deployment_delete
    DeploymentControlKind.Start -> R.string.deployment_start
    DeploymentControlKind.Stop -> R.string.deployment_stop
    DeploymentControlKind.Restart -> R.string.deployment_restart
    DeploymentControlKind.Cancel -> R.string.common_cancel
})
