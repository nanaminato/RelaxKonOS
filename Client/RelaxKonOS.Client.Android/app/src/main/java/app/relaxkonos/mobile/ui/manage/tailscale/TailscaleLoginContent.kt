package app.relaxkonos.mobile.ui.manage.tailscale

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

@OptIn(ExperimentalLayoutApi::class)
@Composable internal fun TailscaleLoginContent(model: TailscaleLoginViewModel, hostMayHaveChanged: () -> Unit) {
    val state = model.state
    val context = LocalContext.current
    Text(stringResource(R.string.tailscale_login_intro))
    state.owner?.let { Text(it.workspaceName) }
    RefreshProgressIndicator(visible = state.busy)
    state.problem?.let { problem ->
        Text(stringResource(when(problem) {
            "tailscale.storage_failed" -> R.string.tailscale_storage_failed
            "tailscale.plan_expired" -> R.string.tailscale_plan_expired
            "tailscale.login.browser_failed" -> R.string.tailscale_login_browser_failed
            "tailscale.login.challenge_unavailable" -> R.string.tailscale_login_challenge_unavailable
            else -> R.string.tailscale_unverified
        }),color = MaterialTheme.colorScheme.error)
    }
    Button(enabled = model.canStart,onClick = model::prepare) { Text(stringResource(R.string.tailscale_login_start)) }
    Text(stringResource(R.string.tailscale_login_records))
    if (state.records.isEmpty()) Text(stringResource(R.string.tailscale_records_empty))
    state.records.forEach { reference ->
        FilterChip(selected = reference.operationId == state.selected,enabled = !state.busy,
            onClick = { model.select(reference.operationId) },label = { Text(reference.operationId) })
    }
    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
        TextButton(enabled = !state.busy && state.selected != null,onClick = model::read) { Text(stringResource(R.string.tailscale_reopen)) }
        TextButton(enabled = model.canOpen,onClick = {
            model.open { url ->
                if (!TailscaleLoginWire.officialUrl(url)) false else try {
                    context.startActivity(Intent(Intent.ACTION_VIEW,Uri.parse(url)).addCategory(Intent.CATEGORY_BROWSABLE)); true
                } catch (_: android.content.ActivityNotFoundException) { false }
                catch (_: SecurityException) { false }
            }
        }) { Text(stringResource(R.string.tailscale_login_open)) }
        TextButton(enabled = model.canInspect,onClick = model::inspect) { Text(stringResource(R.string.tailscale_inspect)) }
    }
    state.operation?.let { operation ->
        Text(stringResource(if (operation.closure != null) R.string.tailscale_login_closed else when(operation.state) {
            TailscaleLoginState.Prepared -> R.string.tailscale_operation_prepared
            TailscaleLoginState.Applying -> R.string.tailscale_operation_applying
            TailscaleLoginState.Issued -> R.string.tailscale_login_issued
            TailscaleLoginState.Completed -> R.string.tailscale_login_completed
            TailscaleLoginState.ApprovalRequired -> R.string.tailscale_needs_approval
            TailscaleLoginState.Unknown -> R.string.tailscale_operation_unknown
            TailscaleLoginState.Rejected -> R.string.tailscale_operation_rejected
        }))
    }
    state.recovery?.let { preview ->
        preview.observation?.let { LoginObservation(it.backendState) }
        TextButton(enabled = !state.busy && preview.canClose && model.canInspect,onClick = model::requestClose) { Text(stringResource(R.string.tailscale_login_close)) }
    }
    if (state.browserOpened) Text(stringResource(R.string.tailscale_login_browser_opened))
    Text(stringResource(R.string.tailscale_login_waiting_boundary))
    val review = model.review
    if (review != null) ConfirmDangerousDialog(stringResource(R.string.tailscale_login_title),
        stringResource(if (review.plan != null) R.string.tailscale_login_start_warning else R.string.tailscale_login_close_warning),
        stringResource(if (review.plan != null) R.string.tailscale_login_start else R.string.tailscale_login_close),
        onConfirm = { model.submit(review,hostMayHaveChanged) },onDismiss = model::dismiss,busy = state.busy,
        extraContent = {
            Text(review.owner.workspaceName); Text(review.owner.serviceId)
            Text(requireNotNull(review.plan?.operationId ?: review.recovery?.operationId))
            review.recovery?.observation?.let { LoginObservation(it.backendState) }
        })
}
@Composable private fun LoginObservation(state: String) {
    Text(stringResource(when(state) {
        "Running" -> R.string.tailscale_connected; "Stopped" -> R.string.tailscale_disconnected
        "NeedsLogin" -> R.string.tailscale_needs_login; "NeedsMachineAuth" -> R.string.tailscale_needs_approval
        else -> R.string.tailscale_unknown
    }))
}
