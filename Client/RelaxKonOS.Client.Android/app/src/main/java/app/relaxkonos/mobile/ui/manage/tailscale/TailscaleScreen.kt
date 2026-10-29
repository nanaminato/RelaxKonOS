package app.relaxkonos.mobile.ui.manage.tailscale

import androidx.compose.material3.*
import androidx.compose.foundation.layout.Column
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.TailscaleRepository
import app.relaxkonos.mobile.ui.common.*
import kotlinx.coroutines.CancellationException

@Composable
internal fun TailscaleDevicesContent(modifier: Modifier = Modifier) {
    val container = appContainer()
    val owner = container.activeSession
    val repository = remember(container) { TailscaleRepository(container.gateway, container.session) }
    var refresh by remember(owner) { mutableIntStateOf(0) }
    var busy by remember(owner) { mutableStateOf(false) }
    var status by remember(owner) { mutableStateOf<TailscaleStatus?>(null) }
    var failed by remember(owner) { mutableStateOf(false) }
    LaunchedEffect(owner, refresh) {
        status = null
        failed = false
        if (owner == null || ServerCapabilities.TAILSCALE !in owner.capabilities) return@LaunchedEffect
        busy = true
        try {
            when (val result = repository.status(owner)) {
                is ApiResult.Success -> status = result.value
                else -> failed = true
            }
        } catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { failed = true }
        finally { busy = false }
    }
    Column(modifier) {
        Text(stringResource(R.string.tailscale_intro))
        if (owner == null || ServerCapabilities.TAILSCALE !in owner.capabilities) {
            Text(stringResource(R.string.error_capability_missing))
            return@Column
        }
        PageActionRow(refresh = {
            TextButton(enabled = !busy, onClick = { refresh++ }) { ActionLabel(R.string.common_refresh) }
        })
        RefreshProgressIndicator(visible = busy)
        if (failed) Text(stringResource(R.string.tailscale_unavailable))
        status?.let { facts ->
            ManagementCard {
                Text(stringResource(when (facts.state) {
                    "observed" -> R.string.tailscale_observed
                    "not-installed" -> R.string.tailscale_not_installed
                    "unsupported" -> R.string.tailscale_unsupported
                    else -> R.string.tailscale_unavailable
                }), style = MaterialTheme.typography.titleMedium)
                facts.version?.let { Text(stringResource(R.string.tailscale_version, it)) }
                facts.backendState?.let { backend ->
                    val label = stringResource(when (backend) {
                        "Running" -> R.string.tailscale_connected
                        "Stopped" -> R.string.tailscale_disconnected
                        "Starting" -> R.string.tailscale_starting
                        "NeedsLogin" -> R.string.tailscale_needs_login
                        "NeedsMachineAuth" -> R.string.tailscale_needs_approval
                        else -> R.string.tailscale_unknown
                    })
                    Text(stringResource(R.string.tailscale_backend, label))
                }
            }
            facts.self?.let { peer ->
                Text(stringResource(R.string.tailscale_self), style = MaterialTheme.typography.titleMedium)
                PeerCard(peer)
            }
            if (facts.peersRestricted) Text(stringResource(R.string.tailscale_restricted))
            else if (facts.state == "observed") {
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
        peer.addresses.forEach { Text(it) }
        Text(stringResource(if (peer.online) R.string.tailscale_online else R.string.tailscale_offline))
        if (peer.exitNode) Text(stringResource(R.string.tailscale_exit_node))
    }
}
