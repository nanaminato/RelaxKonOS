package app.relaxkonos.mobile.ui.manage.websites

import app.relaxkonos.mobile.ui.common.*
import androidx.compose.runtime.saveable.rememberSaveable
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
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.ui.manage.certificates.*
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.net.usageProblem
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DeploymentApplication
import app.relaxkonos.mobile.core.net.ManagedCertificate
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.core.net.WebServer
import app.relaxkonos.mobile.core.net.WebServerConfigTest
import app.relaxkonos.mobile.core.net.WebServerSite
import app.relaxkonos.mobile.core.net.WebServerStatus
import app.relaxkonos.mobile.core.net.WebsitePublishRequest
import app.relaxkonos.mobile.core.net.WebsitePublicationOperation
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay

internal data class WebsiteServerState(
    val server: WebServer,
    val status: ApiResult<WebServerStatus>,
    val configuration: ApiResult<WebServerConfigTest>?,
    val sites: ApiResult<List<WebServerSite>>,
)

internal data class WebsitesState(
    val loading: Boolean = false,
    val servers: ApiResult<List<WebsiteServerState>>? = null,
    val certificates: ApiResult<List<ManagedCertificate>>? = null,
    val applications: ApiResult<List<DeploymentApplication>>? = null,
    val selectedServerId: String? = null,
    val selectedApplicationId: String? = null,
    val publication: ApiResult<WebsitePublicationOperation>? = null,
    val publicationHistory: ApiResult<List<WebsitePublicationOperation>>? = null,
    val publishing: Boolean = false,
)

internal class WebsitesViewModel(application: Application) : AndroidViewModel(application) {
    private val container: AppContainer get() = getApplication<RelaxKonApplication>().container
    var state by mutableStateOf(WebsitesState())
        private set

    var sessionEpoch by androidx.compose.runtime.mutableIntStateOf(0)
        private set
    init {
        viewModelScope.launch { container.session.state.collect { state = WebsitesState(); sessionEpoch++ } }
    }

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
            val firstServer = (details as? ApiResult.Success)?.value?.firstOrNull { it.server.canRead && it.server.canTestConfiguration }?.server?.id
            val firstApplication = (applications as? ApiResult.Success)?.value?.firstOrNull { it.actualState.equals("running", true) }?.id
            if (container.activeSession !== owner) return@launch
            state = state.copy(loading = false,
                servers = details, certificates = certificates, applications = applications,
                selectedServerId = state.selectedServerId ?: firstServer,
                selectedApplicationId = state.selectedApplicationId ?: firstApplication,
            )
        }
    }

    fun selectServer(serverId: String) { state = state.copy(selectedServerId = serverId) }
    fun selectApplication(applicationId: String) {
        state = state.copy(selectedApplicationId = applicationId, publication = null, publicationHistory = null)
        loadHistory(applicationId)
    }

    fun publish(domain: String, contactEmail: String, certificateId: String?, acceptedTerms: Boolean, publiclyReachable: Boolean) {
        val owner = container.activeSession ?: return
        val serverId = state.selectedServerId ?: return
        val applicationId = state.selectedApplicationId ?: return
        if (state.publishing) return
        state = state.copy(publishing = true, publication = null)
        viewModelScope.launch {
            try {
                if (certificateId != null) {
                    val certificate = container.certificates.certificate(owner, certificateId)
                    if (container.activeSession !== owner) return@launch
                    if (certificate !is ApiResult.Success || certificate.value.usageProblem(listOf(domain)) != null) {
                        state = state.copy(publishing = false, publication = ApiResult.Problem(409, "webserver.site_certificate_unverified", null)); return@launch
                    }
                }
                val result = container.webPublishing.publish(WebsitePublishRequest(
                    applicationId = applicationId, webServerId = serverId, domain = domain,
                    certificateId = certificateId, contactEmail = contactEmail, acceptedTerms = acceptedTerms,
                    publicReachabilityConfirmed = publiclyReachable, confirmed = true,
                ), container.elevationAnswers)
                if (container.activeSession !== owner) return@launch
                state = state.copy(publishing = false, publication = result)
                if (result is ApiResult.Success) loadHistory(result.value.applicationId)
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (container.activeSession === owner) state = state.copy(publication = ApiResult.Transport(null)) }
            finally { if (container.activeSession === owner) state = state.copy(publishing = false) }
        }
    }

    fun refreshPublication(operationId: String) {
        val owner = container.activeSession ?: return
        viewModelScope.launch {
            val result = container.webPublishing.operation(owner, operationId)
            if (container.activeSession !== owner) return@launch
            state = state.copy(publication = result)
            if (result is ApiResult.Success && result.value.isTerminal()) loadHistory(result.value.applicationId)
        }
    }

    private fun loadHistory(applicationId: String) {
        val owner = container.activeSession ?: return
        viewModelScope.launch {
            val result = container.webPublishing.history(owner, applicationId)
            if (container.activeSession === owner && state.selectedApplicationId == applicationId) state = state.copy(publicationHistory = result)
        }
    }

}

@Composable
fun WebsitesScreen(onBack: (() -> Unit)?, modifier: Modifier = Modifier, initialApplicationId: String? = null) {
    val viewModel: WebsitesViewModel = viewModel()
    val state = viewModel.state
    val container = app.relaxkonos.mobile.ui.common.appContainer()
    var section by rememberSaveable(container.activeSession, viewModel.sessionEpoch) { mutableStateOf(if (initialApplicationId == null) "instances" else "publish") }
    androidx.compose.runtime.LaunchedEffect(initialApplicationId) { if (initialApplicationId != null) section = "publish" }
    val available = container.capabilities.contains(ServerCapabilities.WEB_SERVER)
    androidx.compose.runtime.LaunchedEffect(container.activeSession, viewModel.sessionEpoch, available) { if (available && state.servers == null) viewModel.refresh() }
    androidx.compose.runtime.LaunchedEffect(initialApplicationId, state.applications) {
        if (!initialApplicationId.isNullOrBlank() && state.applications is ApiResult.Success &&
            state.selectedApplicationId != initialApplicationId) viewModel.selectApplication(initialApplicationId)
    }

    WorkspaceColumn(stringResource(R.string.websites_title), onBack, listOf(WorkspaceDestination("instances", R.string.workspace_instances), WorkspaceDestination("sites", R.string.workspace_sites), WorkspaceDestination("publish", R.string.workspace_publish), WorkspaceDestination("records", R.string.workspace_records)), section, { section = it }, modifier, stateKey = container.activeSession to viewModel.sessionEpoch) {
        if (!available) {
            EmptyHint(stringResource(R.string.error_capability_missing))
            return@WorkspaceColumn
        }
        Text(stringResource(R.string.websites_publish_note), style = MaterialTheme.typography.bodySmall)
        if (state.loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        WorkspaceSection(section != "publish") { NginxManager(onChanged = viewModel::refresh, section = section, onRecords = { section = "records" }) }
        WorkspaceSection(section == "publish") { androidx.compose.runtime.key(viewModel.sessionEpoch) { WebsitePublisher(state, viewModel) { section = "records" } } }
        WorkspaceSection(section == "records") { SectionCard(stringResource(R.string.websites_publish_title)) { PublicationResult(state, viewModel) } }
    }
}

@Composable
private fun WebsitePublisher(state: WebsitesState, viewModel: WebsitesViewModel, onSubmitted: () -> Unit) {
    val servers = (state.servers as? ApiResult.Success)?.value.orEmpty().filter { it.server.canRead && it.server.canTestConfiguration }
    val applications = (state.applications as? ApiResult.Success)?.value.orEmpty().filter { it.actualState.equals("running", true) }
    if (servers.isEmpty() || applications.isEmpty()) return
    var domain by remember { mutableStateOf("") }
    var email by remember { mutableStateOf("") }
    var useExistingCertificate by remember { mutableStateOf(false) }
    var acceptedTerms by remember { mutableStateOf(false) }
    var publicReachability by remember { mutableStateOf(false) }
    val certificates = (state.certificates as? ApiResult.Success)?.value.orEmpty()
    var selectedCertificateId by remember { mutableStateOf<String?>(null) }
    val selectedCertificate = certificates.firstOrNull { it.id == selectedCertificateId }
    val certificateReady = certificateSelectionProblem(state.certificates, selectedCertificateId, listOf(domain)) == null
    SectionCard(stringResource(R.string.websites_publish_title)) {
        Text(stringResource(R.string.websites_publish_intro), style = MaterialTheme.typography.bodySmall)
        Text(stringResource(R.string.websites_publish_server), style = MaterialTheme.typography.labelLarge)
        servers.forEach { item ->
            TextButton(onClick = { viewModel.selectServer(item.server.id) }) {
                Text(if (item.server.id == state.selectedServerId) "✓ ${item.server.type}" else item.server.type)
            }
        }
        Text(stringResource(R.string.websites_publish_application), style = MaterialTheme.typography.labelLarge)
        applications.forEach { app ->
            TextButton(onClick = { viewModel.selectApplication(app.id) }) {
                Text(if (app.id == state.selectedApplicationId) "✓ ${app.name}" else app.name)
            }
        }
        OutlinedTextField(domain, { domain = it.trim() }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.websites_domain)) }, singleLine = true)
        androidx.compose.foundation.layout.Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
            Checkbox(checked = useExistingCertificate, onCheckedChange = { useExistingCertificate = it }, enabled = !state.publishing)
            Text(stringResource(R.string.websites_use_existing_certificate))
        }
        if (useExistingCertificate) ManagedCertificatePicker(state.certificates, listOf(domain), selectedCertificateId, !state.publishing) { selectedCertificateId = it }
        if (!useExistingCertificate) {
            OutlinedTextField(email, { email = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.websites_contact_email)) }, singleLine = true)
            androidx.compose.foundation.layout.Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                Checkbox(checked = acceptedTerms, onCheckedChange = { acceptedTerms = it })
                Text(stringResource(R.string.websites_accept_terms))
            }
            androidx.compose.foundation.layout.Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                Checkbox(checked = publicReachability, onCheckedChange = { publicReachability = it })
                Text(stringResource(R.string.websites_public_reachability))
            }
        }
        Button(
            enabled = !state.publishing && domain.isNotBlank() && ((useExistingCertificate && certificateReady) || (!useExistingCertificate && email.isNotBlank() && acceptedTerms && publicReachability)),
            onClick = { viewModel.publish(domain, email, if (useExistingCertificate) selectedCertificate?.id else null, acceptedTerms, publicReachability); onSubmitted() },
        ) { Text(stringResource(if (state.publishing) R.string.websites_publishing else R.string.websites_publish)) }
    }
}

@Composable
private fun PublicationResult(state: WebsitesState, viewModel: WebsitesViewModel) {
    val result = state.publication
    if (result is ApiResult.Success) {
        val operation = result.value
        Text(stringResource(R.string.websites_operation, operation.state, operation.stage), style = MaterialTheme.typography.bodyMedium)
        if (operation.problemCode.isNotBlank()) Text(operation.problemCode, color = MaterialTheme.colorScheme.error)
        operation.checks.forEach { check -> Text(stringResource(R.string.websites_check, check.name, check.observer, check.state, check.problemCode.ifBlank { "—" }), style = MaterialTheme.typography.bodySmall) }
        androidx.compose.runtime.LaunchedEffect(operation.operationId, operation.state) {
            if (!operation.isTerminal()) {
                delay(1_000)
                viewModel.refreshPublication(operation.operationId)
            }
        }
    } else if (result is ApiResult.Problem) {
        Text(result.code, color = MaterialTheme.colorScheme.error)
    } else if (result is ApiResult.Transport) {
        Text(stringResource(R.string.websites_publish_transport_failed), color = MaterialTheme.colorScheme.error)
    }
    (state.publicationHistory as? ApiResult.Success)?.value?.take(3)?.forEach { history ->
        Text(stringResource(R.string.websites_operation_history, history.domain, history.state, history.stage), style = MaterialTheme.typography.bodySmall)
    }
}

private fun WebsitePublicationOperation.isTerminal(): Boolean = state in setOf("succeeded", "partialFailed", "failed", "cancelled", "interrupted")

@Composable
private fun WebsiteServers(
    state: WebsitesState,
    certificates: ApiResult<List<ManagedCertificate>>?,
    applications: ApiResult<List<DeploymentApplication>>?,
) = when (val servers = state.servers) {
    null -> SectionCard(stringResource(R.string.websites_servers)) { ActivityIndicator(stringResource(R.string.common_loading)) }
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
        is ApiResult.Success -> ExecutionStatusChip(stringResource(
            if (status.value.runtimeState.equals("running", true)) R.string.websites_runtime_running else R.string.websites_runtime_not_running,
        ), status.value.runtimeState, task = false)
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
    SiteCertificateBinding(site, certificates)
    if (site.routes.isNotEmpty()) Text(stringResource(R.string.websites_route_count, site.routes.size), style = MaterialTheme.typography.bodySmall)
}
