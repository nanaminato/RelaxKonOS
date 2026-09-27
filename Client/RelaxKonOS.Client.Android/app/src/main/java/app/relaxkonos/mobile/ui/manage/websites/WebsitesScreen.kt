package app.relaxkonos.mobile.ui.manage.websites

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DeploymentApplication
import app.relaxkonos.mobile.core.net.ManagedCertificate
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.core.net.WebServer
import app.relaxkonos.mobile.core.net.WebServerConfigTest
import app.relaxkonos.mobile.core.net.WebServerSite
import app.relaxkonos.mobile.core.net.WebServerStatus
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.launch

private data class WebsiteServerState(
    val server: WebServer,
    val status: ApiResult<WebServerStatus>,
    val configuration: ApiResult<WebServerConfigTest>?,
    val sites: ApiResult<List<WebServerSite>>,
)

private data class WebsitesState(
    val loading: Boolean = false,
    val servers: ApiResult<List<WebsiteServerState>>? = null,
    val certificates: ApiResult<List<ManagedCertificate>>? = null,
    val applications: ApiResult<List<DeploymentApplication>>? = null,
)

/** AD05-M1: host-observed publication diagnostics. No action in this screen changes a site or certificate. */
private class WebsitesViewModel(application: Application) : AndroidViewModel(application) {
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container
    var state by mutableStateOf(WebsitesState())
        private set

    fun refresh() {
        val owner = container.activeSession ?: return
        if (state.loading) return
        state = state.copy(loading = true)
        viewModelScope.launch {
            val servers = container.webPublishing.servers(owner)
            val details = when (servers) {
                is ApiResult.Success -> ApiResult.Success(servers.value.map { server ->
                    WebsiteServerState(
                        server = server,
                        status = container.webPublishing.status(owner, server.id),
                        configuration = if (server.canTestConfiguration) container.webPublishing.configTest(owner, server.id) else null,
                        sites = if (server.canRead) container.webPublishing.sites(owner, server.id)
                        else ApiResult.Transport("Server did not grant site-read capability."),
                    )
                })
                is ApiResult.Problem -> servers
                is ApiResult.Transport -> servers
            }
            val certificates = if (owner.capabilities.contains(ServerCapabilities.CERTIFICATES)) container.webPublishing.certificates(owner) else null
            val applications = if (owner.capabilities.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS)) container.deployments.applications(owner) else null
            state = WebsitesState(servers = details, certificates = certificates, applications = applications)
        }
    }

}

@Composable
fun WebsitesScreen(onBack: (() -> Unit)?, modifier: Modifier = Modifier) {
    val viewModel: WebsitesViewModel = viewModel()
    val state = viewModel.state
    val container = app.relaxkonos.mobile.ui.common.appContainer()
    val available = container.capabilities.contains(ServerCapabilities.WEB_SERVER)
    androidx.compose.runtime.LaunchedEffect(available) { if (available && state.servers == null) viewModel.refresh() }

    Column(
        modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.md),
    ) {
        ScreenHeader(
            title = stringResource(R.string.websites_title), onBack = onBack,
            trailing = { TextButton(onClick = viewModel::refresh, enabled = available && !state.loading) { Text(stringResource(R.string.common_refresh)) } },
        )
        if (!available) {
            EmptyHint(stringResource(R.string.error_capability_missing))
            return@Column
        }
        Text(stringResource(R.string.websites_read_only_note), style = MaterialTheme.typography.bodySmall)
        if (state.loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        WebsiteServers(state, state.certificates, state.applications)
    }
}

@Composable
private fun WebsiteServers(
    state: WebsitesState,
    certificates: ApiResult<List<ManagedCertificate>>?,
    applications: ApiResult<List<DeploymentApplication>>?,
) = when (val servers = state.servers) {
    null -> SectionCard(stringResource(R.string.websites_servers)) { Text(stringResource(R.string.common_loading)) }
    is ApiResult.Success -> if (servers.value.isEmpty()) {
        SectionCard(stringResource(R.string.websites_servers)) { Text(stringResource(R.string.websites_no_servers)) }
    } else {
        servers.value.forEach { server -> WebsiteServerCard(server, certificates, applications) }
    }
    else -> SectionCard(stringResource(R.string.websites_servers)) { Text(stringResource(R.string.websites_load_failed), color = MaterialTheme.colorScheme.error) }
}

@Composable
private fun WebsiteServerCard(
    state: WebsiteServerState,
    certificates: ApiResult<List<ManagedCertificate>>?,
    applications: ApiResult<List<DeploymentApplication>>?,
) = SectionCard(state.server.type) {
    val version = state.server.version ?: stringResource(R.string.websites_version_unknown)
    Text(stringResource(R.string.websites_server_details, state.server.managementMode, version), style = MaterialTheme.typography.bodySmall)
    when (val status = state.status) {
        is ApiResult.Success -> Text(stringResource(
            if (status.value.runtimeState.equals("running", true)) R.string.websites_runtime_running else R.string.websites_runtime_not_running,
        ))
        else -> Text(stringResource(R.string.websites_runtime_unknown), color = MaterialTheme.colorScheme.error)
    }
    when (val configuration = state.configuration) {
        null -> Text(stringResource(R.string.websites_config_unsupported), style = MaterialTheme.typography.bodySmall)
        is ApiResult.Success -> Text(stringResource(if (configuration.value.valid) R.string.websites_config_valid else R.string.websites_config_invalid))
        else -> Text(stringResource(R.string.websites_config_unknown), color = MaterialTheme.colorScheme.error)
    }
    when (val sites = state.sites) {
        is ApiResult.Success -> if (sites.value.isEmpty()) Text(stringResource(R.string.websites_no_sites)) else sites.value.forEach { site ->
            WebsiteSite(site, certificates, applications)
        }
        else -> Text(stringResource(R.string.websites_sites_unavailable), color = MaterialTheme.colorScheme.error)
    }
}

@Composable
private fun WebsiteSite(
    site: WebServerSite,
    certificates: ApiResult<List<ManagedCertificate>>?,
    applications: ApiResult<List<DeploymentApplication>>?,
) {
    val bindings = site.bindings.joinToString(", ") { binding -> "${binding.domain}:${binding.port}" }
    ListRow(site.name, subtitle = bindings)
    when (applications) {
        is ApiResult.Success -> {
            val linked = applications.value.filter { it.siteId == site.id }
            if (linked.isEmpty()) Text(stringResource(R.string.websites_no_linked_application), style = MaterialTheme.typography.bodySmall)
            else linked.forEach { application -> Text(stringResource(R.string.websites_linked_application, application.name), style = MaterialTheme.typography.bodySmall) }
        }
        else -> Text(stringResource(R.string.websites_application_association_unverified), style = MaterialTheme.typography.bodySmall)
    }
    if (!site.httpsEnabled) {
        Text(stringResource(R.string.websites_tls_disabled), style = MaterialTheme.typography.bodySmall)
    } else {
        val certificate = (certificates as? ApiResult.Success)?.value?.firstOrNull { it.id == site.certificateId }
        Text(
            stringResource(if (certificate == null) R.string.websites_certificate_unverified else R.string.websites_certificate_status,
                certificate?.primaryDomain ?: "", certificate?.status ?: ""),
            style = MaterialTheme.typography.bodySmall,
        )
    }
    if (site.routes.isNotEmpty()) Text(stringResource(R.string.websites_route_count, site.routes.size), style = MaterialTheme.typography.bodySmall)
}
