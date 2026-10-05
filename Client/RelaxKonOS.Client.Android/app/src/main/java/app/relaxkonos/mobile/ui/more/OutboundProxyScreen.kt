package app.relaxkonos.mobile.ui.more

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.theme.Spacing

/** Compose keys normally compare Active structurally; login ownership is an identity boundary. */
private class ProxyOwnerKey(private val owner: SessionState.Active?) {
    override fun equals(other: Any?): Boolean = other is ProxyOwnerKey && other.owner === owner
    override fun hashCode(): Int = System.identityHashCode(owner)
}

@Composable
fun OutboundProxyScreen(onBack: (() -> Unit)?, onOpenManagedProxy: (() -> Unit)?, modifier: Modifier = Modifier) {
    val container = appContainer()
    val owner = container.activeSession
    val available = owner?.capabilities?.contains(ServerCapabilities.DOCKER) == true
    val scope = rememberCoroutineScope()
    val ownerKey = ProxyOwnerKey(owner)
    val editor = remember(ownerKey, scope) {
        owner?.let { OutboundProxyEditor(container.docker, it, { container.activeSession === it }, scope) }
    }
    DisposableEffect(editor) { onDispose { editor?.close() } }
    var showUrls by remember(ownerKey) { mutableStateOf(false) }
    LaunchedEffect(editor) { if (available) editor?.load() }
    BackHandler(enabled = onBack != null && editor != null) { editor?.leave { onBack?.invoke() } }
    Column(modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md)) {
        ScreenHeader(stringResource(R.string.proxy_title), onBack = onBack?.let { { editor?.leave(it) } },
            trailing = { TextButton(onClick = { editor?.refresh() }, enabled = available && editor != null && !editor.busy && editor.review == null) {
                ActionLabel(R.string.common_refresh)
            } })
        if (owner == null || ServerCapabilities.DOCKER !in owner.capabilities || editor == null) {
            EmptyHint(stringResource(R.string.error_capability_missing)); return@Column
        }
        Text(stringResource(R.string.proxy_host_scope))
        editor.message?.let { ActionFeedback(it, onRetry = null, onDismiss = editor::dismissMessage) }
        RefreshProgressIndicator(visible = editor.busy)
        val status = editor.status
        val draft = editor.draft
        if (draft != null) {
            val editable = editor.canSubmit
            SectionCard(stringResource(R.string.proxy_preference)) {
                ProxyToggle(R.string.proxy_enabled, draft.enabled, editable) { value -> editor.change { it.copy(enabled = value) } }
                if (draft.source == OutboundProxySource.ManagedProxy) {
                    Text(stringResource(R.string.proxy_managed_source))
                    if (status != null) Text(stringResource(if (status.managedProxyAvailable) R.string.proxy_managed_available else R.string.proxy_problem_managed_proxy_unavailable))
                    TextButton(onClick = { editor.change { it.copy(source = OutboundProxySource.Custom, httpProxy = "", httpsProxy = "") } }, enabled = editable) {
                        Text(stringResource(R.string.proxy_use_custom))
                    }
                } else {
                    Text(stringResource(R.string.proxy_custom_source))
                    if (ServerCapabilities.PROXY in owner.capabilities) TextButton(onClick = editor::selectManaged, enabled = editable) { Text(stringResource(R.string.proxy_use_managed)) }
                }
                if (ServerCapabilities.PROXY in owner.capabilities && onOpenManagedProxy != null) {
                    TextButton(enabled = !editor.busy, onClick = { editor.leave(onOpenManagedProxy) }) { Text(stringResource(R.string.proxy_manage_mihomo)) }
                    Text(stringResource(R.string.proxy_managed_note), style = MaterialTheme.typography.bodySmall)
                }
                if (draft.source == OutboundProxySource.ManagedProxy && status != null) {
                    KeyValueRow(stringResource(R.string.proxy_managed_endpoint), status.managedProxyEndpoint.ifEmpty { stringResource(R.string.proxy_empty) })
                }
                ProxyToggle(R.string.proxy_show_urls, showUrls, !editor.busy) { showUrls = it }
                val custom = editable && draft.source == OutboundProxySource.Custom
                ProxyUrlField(R.string.proxy_http, draft.httpProxy, custom, showUrls) { value -> editor.change { it.copy(httpProxy = value) } }
                ProxyUrlField(R.string.proxy_https, draft.httpsProxy, custom, showUrls) { value -> editor.change { it.copy(httpsProxy = value) } }
                Text(stringResource(R.string.proxy_https_hint), style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(draft.noProxy, { value -> editor.change { it.copy(noProxy = value) } },
                    label = { Text(stringResource(R.string.proxy_no_proxy)) }, enabled = editable, modifier = Modifier.fillMaxWidth())
                Text(stringResource(R.string.proxy_no_proxy_hint), style = MaterialTheme.typography.bodySmall)
                ProxyToggle(R.string.proxy_engine, draft.applyToEngine, editable) { value -> editor.change { it.copy(applyToEngine = value) } }
                ProxyToggle(R.string.proxy_build, draft.applyToBuild, editable) { value -> editor.change { it.copy(applyToBuild = value) } }
                ProxyToggle(R.string.proxy_image_tags, draft.applyToImageTags, editable) { value -> editor.change { it.copy(applyToImageTags = value) } }
                ProxyToggle(R.string.proxy_runtime_downloads, draft.applyToRuntimeDownloads, editable) { value -> editor.change { it.copy(applyToRuntimeDownloads = value) } }
                Button(onClick = editor::save, enabled = editor.canSubmit, modifier = Modifier.fillMaxWidth()) { ActionLabel(R.string.common_save) }
                OutlinedButton(onClick = editor::clear, enabled = editor.canSubmit, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.proxy_clear)) }
            }
        }
        status?.let { ProxyStatusCard(it, showUrls) }
    }
    editor?.review?.let { pending ->
        val discard = pending == ProxyReview.Refresh || pending == ProxyReview.Leave
        AlertDialog(onDismissRequest = editor::dismiss,
            title = { Text(stringResource(if (discard) R.string.proxy_discard_title else R.string.proxy_confirm_title)) },
            text = { Text(stringResource(when {
                discard -> R.string.proxy_discard_message
                pending == ProxyReview.Clear -> R.string.proxy_confirm_clear
                // Removal can restart an engine even when the last read reports Disabled: the
                // private server installation marker is not part of the public status contract.
                else -> R.string.proxy_confirm_save
            })) },
            confirmButton = { Button(onClick = { editor.confirm(onBack) }) {
                Text(stringResource(if (discard) R.string.proxy_discard else R.string.proxy_apply))
            } },
            dismissButton = { TextButton(onClick = editor::dismiss) { Text(stringResource(R.string.common_cancel)) } })
    }
}

@Composable private fun ProxyToggle(label: Int, checked: Boolean, enabled: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.md), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
        Text(stringResource(label), Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}
@Composable private fun ProxyUrlField(label: Int, value: String, enabled: Boolean, show: Boolean, onChange: (String) -> Unit) {
    OutlinedTextField(value, onChange, label = { Text(stringResource(label)) }, enabled = enabled,
        visualTransformation = if (show) VisualTransformation.None else PasswordVisualTransformation(),
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
        singleLine = true, modifier = Modifier.fillMaxWidth())
}
@Composable private fun ProxyStatusCard(status: OutboundProxyStatus, showUrls: Boolean) {
    SectionCard(stringResource(R.string.proxy_actual_status)) {
        KeyValueRow(stringResource(R.string.home_label_platform), status.platform)
        listOf(OutboundProxyTarget.Engine to R.string.proxy_engine, OutboundProxyTarget.Build to R.string.proxy_build).forEach { (target, label) ->
            val layer = status.layers.firstOrNull { it.target == target }
            KeyValueRow(stringResource(label), stringResource(when (layer?.state) {
                OutboundProxyLayerState.Disabled -> R.string.proxy_state_disabled
                OutboundProxyLayerState.Applied -> R.string.proxy_state_applied
                OutboundProxyLayerState.RestartRequired -> R.string.proxy_state_restart_required
                OutboundProxyLayerState.Unsupported -> R.string.proxy_state_unsupported
                OutboundProxyLayerState.Failed -> R.string.proxy_state_failed
                null -> R.string.proxy_state_unknown
            }))
            if (!layer?.problemCode.isNullOrEmpty()) Text(proxyProblem(layer!!.problemCode).text())
            layer?.detail?.takeIf { it.isNotEmpty() }?.let { Text(proxyDetail(it).text()) }
        }
        val settings = status.settings
        // These two consumers have no observed layer in DockerProxyStatusDto. Label their saved
        // policy honestly; a successful socket or download is not reported by this endpoint.
        KeyValueRow(stringResource(R.string.proxy_image_tags), stringResource(if (settings.enabled && settings.applyToImageTags) R.string.proxy_scope_selected else R.string.proxy_state_disabled))
        KeyValueRow(stringResource(R.string.proxy_runtime_downloads), stringResource(if (settings.enabled && settings.applyToRuntimeDownloads) R.string.proxy_scope_selected else R.string.proxy_state_disabled))
        Text(stringResource(R.string.proxy_scope_status_hint), style = MaterialTheme.typography.bodySmall)
        Text(stringResource(R.string.proxy_effective_engine), style = MaterialTheme.typography.titleSmall)
        ProxyReportedUrl(R.string.proxy_http, status.effectiveHttpProxy, showUrls)
        ProxyReportedUrl(R.string.proxy_https, status.effectiveHttpsProxy, showUrls)
        KeyValueRow(stringResource(R.string.proxy_no_proxy), status.effectiveNoProxy.ifEmpty { stringResource(R.string.proxy_empty) })
        status.desktopProxy?.let { desktop ->
            Text(stringResource(R.string.proxy_desktop), style = MaterialTheme.typography.titleSmall)
            KeyValueRow(stringResource(R.string.proxy_desktop_mode), stringResource(if (desktop.mode.equals("manual", true)) R.string.proxy_desktop_manual else R.string.proxy_desktop_system))
            ProxyReportedUrl(R.string.proxy_http, desktop.httpProxy, showUrls)
            ProxyReportedUrl(R.string.proxy_https, desktop.httpsProxy, showUrls)
            KeyValueRow(stringResource(R.string.proxy_no_proxy), desktop.noProxy.ifEmpty { stringResource(R.string.proxy_empty) })
        }
    }
}
@Composable private fun ProxyReportedUrl(label: Int, value: String, show: Boolean) {
    KeyValueRow(stringResource(label), if (value.isEmpty()) stringResource(R.string.proxy_empty) else if (show) value else stringResource(R.string.proxy_hidden))
}
