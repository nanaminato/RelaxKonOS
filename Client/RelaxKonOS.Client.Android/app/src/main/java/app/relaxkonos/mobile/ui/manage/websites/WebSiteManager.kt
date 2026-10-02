package app.relaxkonos.mobile.ui.manage.websites

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.ui.manage.certificates.*
import app.relaxkonos.mobile.ui.theme.Spacing
import app.relaxkonos.mobile.RelaxKonApplication
import androidx.compose.ui.platform.LocalContext
import app.relaxkonos.mobile.data.ExternalServiceAddresses

@Composable
internal fun WebSiteList(server: WebServer, state: NginxState, canManage: Boolean, model: NginxViewModel, confirm: (Int, () -> Unit) -> Unit) {
    val container = (LocalContext.current.applicationContext as RelaxKonApplication).container
    val owner = container.activeSession
    Text(stringResource(R.string.websites_site_manager), style = MaterialTheme.typography.titleSmall)
    if (canManage && server.canRead && server.canTestConfiguration) OutlinedButton(enabled = !state.busy,
        onClick = { model.editSite(server) }) { Text(stringResource(R.string.websites_site_create)) }
    TextButton(enabled = !state.busy, onClick = { model.inspectSites(server.id) }) { Text(stringResource(R.string.common_refresh)) }
    when (val sites = state.sites[server.id]) {
        is ApiResult.Success -> {
            if (sites.value.isEmpty()) Text(stringResource(R.string.websites_no_sites))
            sites.value.forEach { site ->
                Text(site.name, style = MaterialTheme.typography.titleSmall)
                Text(site.bindings.joinToString(", ") { "${it.domain}:${it.port}" }, style = MaterialTheme.typography.bodySmall)
                Text(site.rootPath ?: stringResource(R.string.websites_site_proxy_only), style = MaterialTheme.typography.bodySmall)
                SiteCertificateBinding(site, state.certificates)
                ServiceAccess(ExternalServiceAddresses.site(site), ready = !state.busy && !state.loading) {
                    owner != null && container.activeSession === owner &&
                        (model.state.sites[server.id] as? ApiResult.Success)?.value?.any { it == site } == true
                }
                site.routes.forEach { Text("${it.path} → ${it.upstream}", style = MaterialTheme.typography.bodySmall) }
                if (canManage && server.canTestConfiguration) Row(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    OutlinedButton(enabled = !state.busy && state.pendingSites.none { it.serverId == server.id && it.siteId == site.id },
                        onClick = { model.editSite(server, site) }) { Text(stringResource(R.string.websites_site_edit)) }
                    TextButton(enabled = !state.busy && state.pendingSites.none { it.serverId == server.id && it.siteId == site.id },
                        onClick = { confirm(R.string.websites_site_delete_confirm) { model.deleteSite(site) } }) { Text(stringResource(R.string.common_delete)) }
                }
            }
        }
        else -> Text(stringResource(R.string.websites_sites_unavailable), color = MaterialTheme.colorScheme.error)
    }
}

@Composable
internal fun WebSiteRecovery(state: NginxState, model: NginxViewModel, confirm: (Int, () -> Unit) -> Unit) {
    state.pendingSites.forEach { pending ->
        Text(stringResource(R.string.websites_site_pending, pending.serverId, pending.siteId), style = MaterialTheme.typography.bodySmall)
        val facts = state.siteFacts[pending.serverId]
        val current = (facts as? ApiResult.Success)?.value?.firstOrNull { it.id == pending.siteId }
        Text(stringResource(when {
            facts !is ApiResult.Success -> R.string.websites_site_facts_unknown
            current == null -> R.string.websites_site_facts_absent
            state.siteDraft?.takeIf { it.id == pending.siteId }?.request()?.matches(current) == true -> R.string.websites_site_facts_match
            else -> R.string.websites_site_facts_present
        }), style = MaterialTheme.typography.bodySmall)
        current?.let { Text("${it.name} · ${it.updatedAt}", style = MaterialTheme.typography.bodySmall) }
        TextButton(enabled = !state.busy, onClick = { model.inspectSites(pending.serverId) }) { Text(stringResource(R.string.websites_site_inspect)) }
        if (facts is ApiResult.Success) TextButton(enabled = !state.busy, onClick = {
            confirm(R.string.websites_site_accept_confirm) { model.acceptSiteFacts(pending) }
        }) { Text(stringResource(R.string.websites_site_accept)) }
    }
}

@Composable
internal fun WebSiteEditor(state: NginxState, model: NginxViewModel) {
    val draft = state.siteDraft ?: return
    val pending = state.pendingSites.any { it.serverId == draft.serverId && it.siteId == draft.id }
    val locked = state.busy || pending
    var discard by remember(draft.id) { mutableStateOf(false) }
    var apply by remember(draft.id) { mutableStateOf(false) }
    var reload by remember(draft.id) { mutableStateOf(false) }
    fun close() { if (!state.busy) { if (draft != state.initialSiteDraft) discard = true else model.closeSiteDraft() } }
    AlertDialog(onDismissRequest = ::close, modifier = Modifier.imePadding(),
        title = { Text(stringResource(if (draft.expectedUpdatedAt == null) R.string.websites_site_create else R.string.websites_site_edit)) },
        text = { Column(Modifier.heightIn(max = 430.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            Text(stringResource(R.string.websites_site_editor_note), style = MaterialTheme.typography.bodySmall)
            OutlinedTextField(draft.name, { model.updateSiteDraft(draft.copy(name = it)) }, enabled = !locked, singleLine = true,
                modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.websites_site_name)) })
            Text(stringResource(R.string.websites_site_bindings))
            draft.bindings.forEachIndexed { index, binding ->
                OutlinedTextField(binding.domain, { text -> model.updateSiteDraft(draft.copy(bindings = draft.bindings.mapIndexed { i, value -> if (i == index) value.copy(domain = text) else value })) },
                    enabled = !locked, singleLine = true, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.websites_domain)) })
                OutlinedTextField(binding.port, { text -> model.updateSiteDraft(draft.copy(bindings = draft.bindings.mapIndexed { i, value -> if (i == index) value.copy(port = text) else value })) },
                    enabled = !locked, singleLine = true, label = { Text(stringResource(R.string.websites_site_port)) })
                if (draft.bindings.size > 1) TextButton(enabled = !locked, onClick = { model.updateSiteDraft(draft.copy(bindings = draft.bindings.filterIndexed { i, _ -> i != index })) }) { Text(stringResource(R.string.websites_site_remove_binding)) }
            }
            OutlinedButton(enabled = !locked && draft.bindings.size < 20, onClick = { model.updateSiteDraft(draft.copy(bindings = draft.bindings + SiteBindingDraft())) }) { Text(stringResource(R.string.websites_site_add_binding)) }
            if (!locked) RemotePathField(draft.rootPath, { model.updateSiteDraft(draft.copy(rootPath = it)) }, R.string.websites_site_root, RemotePathKind.Directory)
            else Text(draft.rootPath)
            SiteCheck(draft.spaFallback, R.string.websites_site_spa, !locked) { model.updateSiteDraft(draft.copy(spaFallback = it)) }
            SiteCheck(draft.grantReadAccess, R.string.websites_site_grant_read, !locked) { model.updateSiteDraft(draft.copy(grantReadAccess = it)) }
            Text(stringResource(R.string.websites_site_routes))
            draft.routes.forEachIndexed { index, route ->
                OutlinedTextField(route.path, { text -> model.updateSiteDraft(draft.copy(routes = draft.routes.mapIndexed { i, value -> if (i == index) value.copy(path = text) else value })) },
                    enabled = !locked, singleLine = true, label = { Text(stringResource(R.string.websites_site_route_path)) })
                OutlinedTextField(route.upstream, { text -> model.updateSiteDraft(draft.copy(routes = draft.routes.mapIndexed { i, value -> if (i == index) value.copy(upstream = text) else value })) },
                    enabled = !locked, singleLine = true, modifier = Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.websites_site_upstream)) })
                SiteCheck(route.disableBuffering, R.string.websites_site_disable_buffering, !locked) { checked ->
                    model.updateSiteDraft(draft.copy(routes = draft.routes.mapIndexed { i, value -> if (i == index) value.copy(disableBuffering = checked) else value }))
                }
                TextButton(enabled = !locked, onClick = { model.updateSiteDraft(draft.copy(routes = draft.routes.filterIndexed { i, _ -> i != index })) }) { Text(stringResource(R.string.websites_site_remove_route)) }
            }
            OutlinedButton(enabled = !locked, onClick = { model.updateSiteDraft(draft.copy(routes = draft.routes + SiteRouteDraft())) }) { Text(stringResource(R.string.websites_site_add_route)) }
            SiteCheck(draft.httpsEnabled, R.string.websites_site_https, !locked) { model.updateSiteDraft(draft.copy(httpsEnabled = it, redirectHttpToHttps = if (it) draft.redirectHttpToHttps else false)) }
            SiteCheck(draft.redirectHttpToHttps, R.string.websites_site_redirect, !locked && draft.httpsEnabled) { model.updateSiteDraft(draft.copy(redirectHttpToHttps = it)) }
            SiteCheck(draft.ipv6Enabled, R.string.websites_site_ipv6, !locked) { model.updateSiteDraft(draft.copy(ipv6Enabled = it)) }
            SiteCheck(draft.useServerCertificate, R.string.websites_site_server_certificate, !locked) { model.updateSiteDraft(draft.copy(useServerCertificate = it)) }
            if (draft.useServerCertificate) {
                if (!locked) {
                    RemotePathField(draft.certificatePath, { model.updateSiteDraft(draft.copy(certificatePath = it)) }, R.string.websites_site_certificate_path, RemotePathKind.File)
                    RemotePathField(draft.privateKeyPath, { model.updateSiteDraft(draft.copy(privateKeyPath = it)) }, R.string.websites_site_key_path, RemotePathKind.File)
                } else { Text(draft.certificatePath); Text(draft.privateKeyPath) }
            } else {
                ManagedCertificatePicker(state.certificates, draft.bindings.map { it.domain }, draft.certificateId, !locked) {
                    model.updateSiteDraft(draft.copy(certificateId = it))
                }
                TextButton(enabled = !state.busy, onClick = model::refreshCertificates) { Text(stringResource(R.string.common_refresh)) }
            }
            Text(stringResource(R.string.websites_site_tls_note), style = MaterialTheme.typography.bodySmall)
            if (draft.request() == null) Text(stringResource(R.string.websites_site_validation), color = MaterialTheme.colorScheme.error)

            if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            if (draft.expectedUpdatedAt != null) {
                TextButton(enabled = !state.busy, onClick = { model.inspectSites(draft.serverId) }) { Text(stringResource(R.string.websites_site_inspect)) }
                if (!pending && (state.sites[draft.serverId] as? ApiResult.Success)?.value?.any { it.id == draft.id } == true)
                    TextButton(enabled = !state.busy, onClick = { reload = true }) { Text(stringResource(R.string.websites_site_reload)) }
            }
        } },
        confirmButton = { Button(enabled = !state.busy && draft.request() != null && (!draft.httpsEnabled || draft.useServerCertificate || certificateSelectionProblem(state.certificates, draft.certificateId, draft.bindings.map { it.domain }) == null), onClick = { apply = true }) { Text(stringResource(if (pending) R.string.common_retry else R.string.common_save)) } },
        dismissButton = { TextButton(enabled = !state.busy, onClick = ::close) { Text(stringResource(R.string.common_close)) } })
    if (discard || reload || apply) AlertDialog(onDismissRequest = { discard = false; reload = false; apply = false },
        title = { Text(stringResource(R.string.nginx_confirm)) }, text = { Text(stringResource(when { apply -> R.string.websites_site_apply_confirm; reload -> R.string.websites_site_reload_confirm; else -> R.string.websites_site_discard_confirm })) },
        confirmButton = { Button(onClick = {
            when { apply -> model.saveSite(); reload -> model.reloadSiteDraft(); else -> model.closeSiteDraft() }
            discard = false; reload = false; apply = false
        }) { Text(stringResource(R.string.nginx_confirm)) } },
        dismissButton = { TextButton(onClick = { discard = false; reload = false; apply = false }) { Text(stringResource(R.string.common_cancel)) } })
}
@Composable
private fun SiteCheck(checked: Boolean, label: Int, enabled: Boolean, update: (Boolean) -> Unit) {
    Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) { Checkbox(checked, update, enabled = enabled); Text(stringResource(label)) }
}
