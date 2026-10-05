package app.relaxkonos.mobile.ui.manage.websites

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import app.relaxkonos.mobile.ui.common.*
import android.app.Application
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.ui.manage.certificates.*
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
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
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
    suspend fun observePublication(operation: WebsitePublicationOperation) {
        if (!operation.isTerminal()) {
            kotlinx.coroutines.delay(1000)
            refreshPublication(operation.operationId)
        }
    }

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

internal fun WebsitePublicationOperation.isTerminal(): Boolean = state in setOf("succeeded", "partialFailed", "failed", "cancelled", "interrupted")
