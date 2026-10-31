package app.relaxkonos.mobile.ui.manage.tailscale

import androidx.compose.foundation.layout.Column
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.TailscalePeer
import app.relaxkonos.mobile.core.net.TailscaleStatus
import app.relaxkonos.mobile.ui.common.*
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale

@Composable
internal fun TailscaleDevicesContent(status: TailscaleStatus?, busy: Boolean, failed: Boolean,
    onRefresh: () -> Unit, onAuthorize: () -> Unit, modifier: Modifier = Modifier) {
    Column(modifier) {
        Text(stringResource(R.string.tailscale_intro))
        PageActionRow(refresh = {
            TextButton(enabled = !busy, onClick = onRefresh) { ActionLabel(R.string.common_refresh) }
        })
        RefreshProgressIndicator(visible = busy)
        if (failed) Text(stringResource(R.string.tailscale_unavailable))
        status?.let { facts ->
            facts.account?.let { account ->
                ManagementCard {
                    Text(account.displayName.ifBlank { account.loginName }, style = MaterialTheme.typography.titleMedium)
                    if (account.loginName.isNotBlank()) Text(account.loginName)
                    facts.tailnetName?.let { Text(stringResource(R.string.tailscale_tailnet, it)) }
                    facts.self?.let {
                        Text(stringResource(R.string.tailscale_this_device, it.hostName, it.addresses.firstOrNull().orEmpty()))
                    }
                }
            }
            ManagementCard {
                Text(stringResource(when (facts.state) {
                    "observed" -> R.string.tailscale_observed
                    "not-installed" -> R.string.tailscale_not_installed
                    "unsupported" -> R.string.tailscale_unsupported
                    else -> R.string.tailscale_unavailable
                }), style = MaterialTheme.typography.titleMedium)
                facts.version?.let { Text(stringResource(R.string.tailscale_version, it)) }
                facts.backendState?.let { backend ->
                    Text(stringResource(R.string.tailscale_backend, backendLabel(backend)))
                }
                facts.exitNode?.let {
                    Text(stringResource(R.string.tailscale_exit_node_current, it.hostName.ifBlank { it.addresses.firstOrNull().orEmpty() }))
                }
            }
            facts.self?.let { peer ->
                Text(stringResource(R.string.tailscale_self), style = MaterialTheme.typography.titleMedium)
                PeerCard(peer)
            }
            if (facts.peersRestricted) {
                Text(stringResource(R.string.tailscale_restricted))
                TextButton(enabled = !busy, onClick = onAuthorize) { Text(stringResource(R.string.tailscale_authorize)) }
            } else if (facts.state == "observed") {
                Text(stringResource(R.string.tailscale_peers), style = MaterialTheme.typography.titleMedium)
                if (facts.peers.isEmpty()) Text(stringResource(R.string.tailscale_empty))
                facts.peers.forEach { PeerCard(it) }
            }
        }
    }
}

@Composable private fun PeerCard(peer: TailscalePeer) {
    ManagementCard {
        Text(peer.hostName, style = MaterialTheme.typography.titleMedium)
        if (peer.dnsName.isNotBlank()) Text(peer.dnsName)
        peer.owner?.let { Text(stringResource(R.string.tailscale_owner_value, it)) }
        peer.os?.let { Text(stringResource(R.string.tailscale_os_value, osLabel(it))) }
        peer.addresses.forEach { Text(it) }
        Text(stringResource(if (peer.expired) R.string.tailscale_key_expired else
            if (peer.online) R.string.tailscale_online else R.string.tailscale_offline))
        if (peer.exitNode) Text(stringResource(R.string.tailscale_exit_node))
        else if (peer.exitNodeOption) Text(stringResource(R.string.tailscale_exit_node_option))
        if (peer.tags.isNotEmpty()) Text(stringResource(R.string.tailscale_tags_value, peer.tags.joinToString(" ") { "#$it" }))
        peer.lastSeen?.let { Text(stringResource(R.string.tailscale_last_seen_value, formatInstant(it))) }
        peer.keyExpiry?.let { Text(stringResource(R.string.tailscale_key_expiry_value, formatInstant(it))) }
    }
}

@Composable private fun backendLabel(backend: String): String = stringResource(when (backend) {
    "Running" -> R.string.tailscale_connected
    "Stopped" -> R.string.tailscale_disconnected
    "Starting" -> R.string.tailscale_starting
    "NeedsLogin" -> R.string.tailscale_needs_login
    "NeedsMachineAuth" -> R.string.tailscale_needs_approval
    else -> R.string.tailscale_unknown
})

private fun osLabel(os: String): String = when (os) {
    "windows" -> "Windows"
    "linux" -> "Linux"
    "macOS" -> "macOS"
    "iOS" -> "iOS"
    "android" -> "Android"
    "tvOS" -> "tvOS"
    "freebsd" -> "FreeBSD"
    "openbsd" -> "OpenBSD"
    else -> os
}

private fun formatInstant(value: Instant): String =
    DateTimeFormatter.ofLocalizedDateTime(FormatStyle.SHORT).withLocale(Locale.getDefault())
        .withZone(ZoneId.systemDefault()).format(value)
