package app.relaxkonos.mobile.ui.manage.websites

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import app.relaxkonos.mobile.ui.common.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.ui.manage.certificates.*
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DeploymentApplication
import app.relaxkonos.mobile.core.net.ManagedCertificate
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.core.net.WebServerSite
import app.relaxkonos.mobile.core.net.WebsitePublicationOperation
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.SectionCard

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
            state.selectedApplicationId != initialApplicationId && state.applications.value.any {
                it.id == initialApplicationId && it.actualState.equals("running", true)
            }) viewModel.selectApplication(initialApplicationId)
    }

    WorkspaceColumn(stringResource(R.string.websites_title), onBack, listOf(WorkspaceDestination("instances", R.string.workspace_instances), WorkspaceDestination("sites", R.string.workspace_sites), WorkspaceDestination("publish", R.string.workspace_publish), WorkspaceDestination("records", R.string.workspace_records)), section, { section = it }, modifier, stateKey = container.activeSession to viewModel.sessionEpoch) {
        if (!available) {
            EmptyHint(stringResource(R.string.error_capability_missing))
            return@WorkspaceColumn
        }
        Text(stringResource(R.string.websites_publish_note), style = MaterialTheme.typography.bodySmall)
        RefreshProgressIndicator(visible = state.loading)
        WorkspaceSection(section != "publish") { NginxManager(onChanged = viewModel::refresh, section = section, onRecords = { section = "records" }) }
        WorkspaceSection(section == "publish") { androidx.compose.runtime.key(viewModel.sessionEpoch) { WebsitePublisher(state, viewModel) { section = "records" } } }
        WorkspaceSection(section == "records") { SectionCard(stringResource(R.string.websites_publish_title)) { PublicationResult(state, viewModel) } }
    }
}

@Composable
private fun WebsitePublisher(state: WebsitesState, viewModel: WebsitesViewModel, onSubmitted: () -> Unit) {
    val servers = (state.servers as? ApiResult.Success)?.value.orEmpty().filter { it.server.canRead && it.server.canTestConfiguration }
    val applications = (state.applications as? ApiResult.Success)?.value.orEmpty().filter { it.actualState.equals("running", true) }
    // Keep the draft in this composition while refresh temporarily shows prerequisite status.
    var domain by remember { mutableStateOf("") }
    var email by remember { mutableStateOf("") }
    var useExistingCertificate by remember { mutableStateOf(false) }
    var acceptedTerms by remember { mutableStateOf(false) }
    var publicReachability by remember { mutableStateOf(false) }
    var selectedCertificateId by remember { mutableStateOf<String?>(null) }
    val prerequisite = websitePublicationPrerequisite(state)
    if (prerequisite == R.string.common_loading) {
        ActivityIndicator(stringResource(R.string.common_loading))
        return
    }
    if (prerequisite != null) {
        SectionCard(stringResource(R.string.websites_publish_title)) {
            Text(stringResource(prerequisite))
            TextButton(onClick = viewModel::refresh, enabled = !state.loading && !state.publishing) {
                ActionLabel(R.string.common_refresh)
            }
        }
        return
    }
    val certificates = (state.certificates as? ApiResult.Success)?.value.orEmpty()
    val selectedCertificate = certificates.firstOrNull { it.id == selectedCertificateId }
    val certificateReady = certificateSelectionProblem(state.certificates, selectedCertificateId, listOf(domain)) == null
    SectionCard(stringResource(R.string.websites_publish_title)) {
        TextButton(onClick = viewModel::refresh, enabled = !state.loading && !state.publishing) {
            ActionLabel(R.string.common_refresh)
        }
        Text(stringResource(R.string.websites_publish_intro), style = MaterialTheme.typography.bodySmall)
        Text(stringResource(R.string.websites_publish_server), style = MaterialTheme.typography.labelLarge)
        servers.forEach { item ->
            TextButton(onClick = { viewModel.selectServer(item.server.id) }, enabled = !state.publishing) {
                Text(if (item.server.id == state.selectedServerId) "✓ ${item.server.type}" else item.server.type)
            }
        }
        Text(stringResource(R.string.websites_publish_application), style = MaterialTheme.typography.labelLarge)
        applications.forEach { app ->
            TextButton(onClick = { viewModel.selectApplication(app.id) }, enabled = !state.publishing) {
                Text(if (app.id == state.selectedApplicationId) "✓ ${app.name}" else app.name)
            }
        }
        OutlinedTextField(domain, { domain = it.trim() }, Modifier.fillMaxWidth(), enabled = !state.publishing, label = { Text(stringResource(R.string.websites_domain)) }, singleLine = true)
        CheckboxOption(useExistingCertificate, stringResource(R.string.websites_use_existing_certificate), !state.publishing) { useExistingCertificate = it }
        if (useExistingCertificate) ManagedCertificatePicker(state.certificates, listOf(domain), selectedCertificateId, !state.publishing) { selectedCertificateId = it }
        if (!useExistingCertificate) {
            OutlinedTextField(email, { email = it }, Modifier.fillMaxWidth(), enabled = !state.publishing, label = { Text(stringResource(R.string.websites_contact_email)) }, singleLine = true)
            CheckboxOption(acceptedTerms, stringResource(R.string.websites_accept_terms), !state.publishing) { acceptedTerms = it }
            CheckboxOption(publicReachability, stringResource(R.string.websites_public_reachability), !state.publishing) { publicReachability = it }
        }
        Button(
            enabled = !state.publishing && domain.isNotBlank() && ((useExistingCertificate && certificateReady) || (!useExistingCertificate && email.isNotBlank() && acceptedTerms && publicReachability)),
            onClick = { viewModel.publish(domain, email, if (useExistingCertificate) selectedCertificate?.id else null, acceptedTerms, publicReachability); onSubmitted() },
        ) { Text(stringResource(if (state.publishing) R.string.websites_publishing else R.string.websites_publish)) }
    }
}

internal fun websitePublicationPrerequisite(state: WebsitesState): Int? = when {
    state.loading -> R.string.common_loading
    state.servers !is ApiResult.Success || state.applications !is ApiResult.Success -> R.string.websites_load_failed
    state.servers.value.none { it.server.canRead && it.server.canTestConfiguration } -> R.string.websites_publish_no_eligible_server
    state.applications.value.none { it.actualState.equals("running", true) } -> R.string.websites_publish_no_running_application
    else -> null
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
            viewModel.observePublication(operation)
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
