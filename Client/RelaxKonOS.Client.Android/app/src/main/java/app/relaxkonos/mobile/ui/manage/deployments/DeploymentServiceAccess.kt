package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.ExternalServiceAddress
import app.relaxkonos.mobile.data.ExternalServiceAddresses
import app.relaxkonos.mobile.ui.common.ServiceAccess
import kotlinx.coroutines.CancellationException

/** Finds the associated site's actual bindings instead of guessing a domain's TLS scheme. */
@Composable
internal fun DeploymentServiceAccess(owner: SessionState.Active, app: DeploymentApplication, stillCurrent: () -> Boolean) {
    val container = (LocalContext.current.applicationContext as RelaxKonApplication).container
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var resumed by remember(lifecycle) { mutableStateOf(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, _ -> resumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer) }
    }
    var refresh by remember(owner, app.id) { mutableIntStateOf(0) }
    var siteAddresses by remember(owner, app.id, app.updatedAt) { mutableStateOf<List<ExternalServiceAddress>?>(null) }
    var siteFailed by remember(owner, app.id, app.updatedAt) { mutableStateOf(false) }
    var checkedAt by remember(owner, app.id, app.updatedAt) { mutableStateOf<Long?>(null) }
    LaunchedEffect(owner, app.id, app.updatedAt, resumed, refresh) {
        siteAddresses = null; siteFailed = false; checkedAt = null
        if (!resumed || app.siteId == null || ServerCapabilities.WEB_SERVER !in owner.capabilities) return@LaunchedEffect
        try {
            val servers = container.webServers.discover(owner)
            val values = (servers as? ApiResult.Success)?.value ?: run { siteFailed = true; return@LaunchedEffect }
            for (server in values.filter { it.canRead }.take(20)) {
                val sites = container.webSites.sites(owner, server.id)
                val site = (sites as? ApiResult.Success)?.value?.firstOrNull { it.id == app.siteId } ?: continue
                if (container.activeSession === owner && stillCurrent()) {
                    siteAddresses = ExternalServiceAddresses.site(site); checkedAt = System.currentTimeMillis()
                }
                return@LaunchedEffect
            }
            siteFailed = true
        } catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { if (container.activeSession === owner && stillCurrent()) siteFailed = true }
    }
    if (app.workloadKind != "web") return
    val addresses = (siteAddresses.orEmpty() + listOfNotNull(ExternalServiceAddresses.deployment(owner, app))).distinctBy { it.url }
    ServiceAccess(addresses, ready = resumed && stillCurrent()) { container.activeSession === owner && stillCurrent() }
    checkedAt?.let { Text(stringResource(R.string.service_access_checked, java.text.DateFormat.getDateTimeInstance().format(java.util.Date(it)))) }
    if (app.siteId != null) {
        TextButton(enabled = resumed, onClick = { refresh++ }) { Text(stringResource(R.string.service_access_refresh)) }
        if (siteFailed) Text(stringResource(R.string.service_access_site_unavailable))
    }
    if (app.hostPort != null && ExternalServiceAddresses.deployment(owner, app) == null) {
        Text(stringResource(R.string.service_access_ssh_note, app.hostPort))
        TextButton(onClick = { if (container.activeSession === owner && stillCurrent()) container.serverCenter.open() }) { Text(stringResource(R.string.service_access_server_center)) }
    }
}
