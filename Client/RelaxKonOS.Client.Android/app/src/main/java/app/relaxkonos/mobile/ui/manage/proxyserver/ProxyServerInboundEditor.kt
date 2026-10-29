package app.relaxkonos.mobile.ui.manage.proxyserver

import androidx.activity.compose.BackHandler
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
import java.util.UUID

@Composable internal fun ProxyServerInboundEditor(configuration: ProxyServerConfiguration, inbound: ProxyServerInbound?, busy: Boolean,
    submit: (SaveProxyServerConfiguration) -> Unit, dismiss: () -> Unit) {
    var name by remember { mutableStateOf(inbound?.name.orEmpty()) }
    var protocol by remember { mutableStateOf(inbound?.protocol ?: ProxyServerProtocol.VlessReality) }
    var address by remember { mutableStateOf(inbound?.listenAddress ?: "0.0.0.0") }
    var port by remember { mutableStateOf(inbound?.listenPort?.toString() ?: "24443") }
    var host by remember { mutableStateOf(inbound?.publicHost.orEmpty()) }
    var publicPort by remember { mutableStateOf(inbound?.publicPort?.toString() ?: "24443") }
    var serverName by remember { mutableStateOf(inbound?.serverName.orEmpty()) }
    var certificate by remember { mutableStateOf(inbound?.certificateId.orEmpty()) }
    var handshake by remember { mutableStateOf(inbound?.handshakeHost.orEmpty()) }
    var handshakePort by remember { mutableStateOf(inbound?.handshakePort?.toString() ?: "443") }
    var path by remember { mutableStateOf(inbound?.webSocketPath ?: "/proxy") }
    var enabled by remember { mutableStateOf(inbound?.enabled ?: true) }
    var error by remember { mutableStateOf(false) }
    var discard by remember { mutableStateOf(false) }
    val dirty = name != inbound?.name.orEmpty() || protocol != (inbound?.protocol ?: ProxyServerProtocol.VlessReality) ||
        address != (inbound?.listenAddress ?: "0.0.0.0") || port != (inbound?.listenPort?.toString() ?: "24443") ||
        host != inbound?.publicHost.orEmpty() || publicPort != (inbound?.publicPort?.toString() ?: "24443") ||
        serverName != inbound?.serverName.orEmpty() || certificate != inbound?.certificateId.orEmpty() ||
        handshake != inbound?.handshakeHost.orEmpty() || handshakePort != (inbound?.handshakePort?.toString() ?: "443") ||
        path != (inbound?.webSocketPath ?: "/proxy") || enabled != (inbound?.enabled ?: true)
    val close = { if (dirty) discard = true else dismiss() }
    BackHandler { close() }
    AlertDialog(onDismissRequest = { if (!busy) close() }, modifier = Modifier.imePadding(),
        title = { Text(stringResource(R.string.proxy_server_inbound_add)) },
        text = { Column(Modifier.heightIn(max = 460.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(stringResource(R.string.proxy_server_inbound_draft_note))
            InboundField(name, R.string.proxy_server_field_name, busy) { name = it }
            ProxyServerProtocol.entries.forEach { option -> RadioOption(protocol == option, proxyServerProtocolLabel(option), !busy) { protocol = option } }
            InboundField(address, R.string.proxy_server_field_address, busy) { address = it }
            InboundField(port, R.string.proxy_server_field_port, busy) { port = it }
            InboundField(host, R.string.proxy_server_field_host, busy) { host = it }
            InboundField(publicPort, R.string.proxy_server_field_public_port, busy) { publicPort = it }
            InboundField(serverName, R.string.proxy_server_field_server_name, busy) { serverName = it }
            if (protocol.requiresCertificate) InboundField(certificate, R.string.proxy_server_field_certificate, busy) { certificate = it }
            else {
                InboundField(handshake, R.string.proxy_server_field_handshake, busy) { handshake = it }
                InboundField(handshakePort, R.string.proxy_server_field_handshake_port, busy) { handshakePort = it }
            }
            if (protocol == ProxyServerProtocol.VlessWebSocket) InboundField(path, R.string.proxy_server_field_path, busy) { path = it }
            CheckboxOption(enabled, stringResource(R.string.proxy_server_credential_enabled), !busy) { enabled = it }
            if (error) Text(stringResource(R.string.proxy_server_inbound_invalid), color = MaterialTheme.colorScheme.error)
        } },
        confirmButton = { Button(enabled = !busy, onClick = {
            val listen = port.toIntOrNull(); val public = publicPort.toIntOrNull(); val handshakeNumber = handshakePort.toIntOrNull()
            val cert = if (protocol.requiresCertificate) runCatching { InstallationRoutes.canonicalId(certificate.trim()) }.getOrNull() else null
            if (name.isBlank() || name.length > 128 || address.isBlank() || host.isBlank() || serverName.isBlank() ||
                listen == null || listen !in 1024..65535 || public == null || public !in 1..65535 ||
                (protocol.requiresCertificate && cert == null) ||
                (protocol == ProxyServerProtocol.VlessReality && (handshake.isBlank() || handshakeNumber == null || handshakeNumber !in 1..65535)) ||
                (protocol == ProxyServerProtocol.VlessWebSocket && !path.startsWith('/'))) { error = true; return@Button }
            val item = ProxyServerInbound(inbound?.id ?: UUID.randomUUID().toString(), name.trim(), protocol, address.trim(), listen,
                host.trim(), public, serverName.trim(), cert, if (protocol == ProxyServerProtocol.VlessReality) handshake.trim() else "",
                if (protocol == ProxyServerProtocol.VlessReality) requireNotNull(handshakeNumber) else 443,
                if (protocol == ProxyServerProtocol.VlessWebSocket) path.trim() else "/proxy", enabled)
            submit(SaveProxyServerConfiguration(configuration.revision, configuration.inbounds.filter { it.id != item.id } + item, configuration.subscriptionBaseUrl))
        }) { Text(stringResource(R.string.common_save)) } },
        dismissButton = { TextButton(enabled = !busy, onClick = close) { Text(stringResource(R.string.common_cancel)) } })
    if (discard) AlertDialog(onDismissRequest = { discard = false }, title = { Text(stringResource(R.string.ui_discard_draft_title)) },
        confirmButton = { TextButton(onClick = dismiss) { Text(stringResource(R.string.editor_discard_changes)) } },
        dismissButton = { TextButton(onClick = { discard = false }) { Text(stringResource(R.string.common_cancel)) } })
}

@Composable private fun InboundField(value: String, label: Int, busy: Boolean, change: (String) -> Unit) {
    OutlinedTextField(value, change, enabled = !busy, label = { Text(stringResource(label)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
}

@Composable internal fun ProxyServerBaseUrlEditor(configuration: ProxyServerConfiguration, busy: Boolean,
    submit: (SaveProxyServerConfiguration) -> Unit, dismiss: () -> Unit) {
    var url by remember { mutableStateOf(configuration.subscriptionBaseUrl.orEmpty()) }
    AlertDialog(onDismissRequest = { if (!busy) dismiss() }, title = { Text(stringResource(R.string.proxy_server_subscription_base)) },
        text = { InboundField(url, R.string.proxy_server_subscription_base, busy) { url = it } },
        confirmButton = { TextButton(enabled = !busy, onClick = { submit(SaveProxyServerConfiguration(configuration.revision, configuration.inbounds, url.trim().ifEmpty { null })) }) { Text(stringResource(R.string.common_save)) } },
        dismissButton = { TextButton(enabled = !busy, onClick = dismiss) { Text(stringResource(R.string.common_cancel)) } })
}
