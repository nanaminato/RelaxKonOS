package app.relaxkonos.mobile.ui.manage.websites

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import app.relaxkonos.mobile.ui.common.*
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.data.*
import androidx.compose.runtime.mutableStateOf
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope

import app.relaxkonos.mobile.ui.manage.certificates.*

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
    val historyLoading: Boolean = false,
    val publishing: Boolean = false,
    val publicationLookupId: String? = null,
    val publicationLoading: Boolean = false,
    val publicationObservation: Int = 0,
)

internal class WebsitesViewModel(private val session: AuthSession, private val webPublishing: WebPublishingRepository,
    private val deployments: DeploymentRepository, private val certificates: CertificateRepository,
    private val elevationAnswers: ElevationAnswerProvider) : ViewModel() {
    suspend fun observePublication(operation: WebsitePublicationOperation, canObserve: () -> Boolean) {
        val owner = session.state.value as? SessionState.Active ?: return
        val request = publicationReadGeneration
        fun current() = session.state.value === owner && request == publicationReadGeneration &&
            state.publicationLookupId == operation.operationId && (state.publication as? ApiResult.Success)?.value == operation &&
            !state.publicationLoading && !state.publishing && canObserve()
        if (!operation.isTerminal() && current()) {
            kotlinx.coroutines.delay(1000)
            if (current()) refreshPublication(operation.operationId)
        }
    }


    var state by mutableStateOf(WebsitesState())
        private set
    private var publicationReadGeneration = 0
    private var historyReadGeneration = 0

    var sessionEpoch by androidx.compose.runtime.mutableIntStateOf(0)
        private set
    init {
        viewModelScope.launch { session.state.collect { publicationReadGeneration++; historyReadGeneration++; state = WebsitesState(); sessionEpoch++ } }
    }

    fun refresh() {
        val owner = (session.state.value as? SessionState.Active) ?: return
        if (state.loading) return
        state = state.copy(loading = true)
        viewModelScope.launch {
            try {
            val servers = webPublishing.servers(owner)
            val details = when (servers) {
                is ApiResult.Success -> ApiResult.Success(servers.value.map { server ->
                    WebsiteServerState(
                        server = server,
                        status = webPublishing.status(owner, server.id),
                        configuration = if (server.canTestConfiguration) webPublishing.configTest(owner, server.id) else null,
                        sites = if (server.canRead) webPublishing.sites(owner, server.id)
                        else ApiResult.Transport("Server did not grant site-read capability."),
                    )
                })
                is ApiResult.Problem -> servers
                is ApiResult.Transport -> servers
            }
            val certificates = if (owner.capabilities.contains(ServerCapabilities.CERTIFICATES)) webPublishing.certificates(owner) else null
            val applications = if (owner.capabilities.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS)) deployments.applications(owner) else null
            val serverIds = (details as? ApiResult.Success)?.value.orEmpty()
                .filter { it.server.canRead && it.server.canTestConfiguration }.map { it.server.id }
            val applicationIds = (applications as? ApiResult.Success)?.value.orEmpty()
                .map { it.id }
            if ((session.state.value as? SessionState.Active) !== owner) return@launch
            val applicationId = websitePublicationSelection(state.selectedApplicationId, applicationIds)
            state = state.copy(loading = false,
                servers = details, certificates = certificates, applications = applications,
                selectedServerId = websitePublicationSelection(state.selectedServerId, serverIds),
            )
            if (state.selectedApplicationId != applicationId) changeApplication(applicationId)
            } catch (cancelled: CancellationException) {
                refreshFailed(owner)
                throw cancelled
            } catch (_: Exception) {
                refreshFailed(owner)
            } finally {
                if (session.state.value === owner) state = state.copy(loading = false)
            }
        }
    }

    private fun refreshFailed(owner: SessionState.Active) {
        if (session.state.value === owner) state = state.copy(servers = ApiResult.Transport(null),
            certificates = null, applications = null)
    }

    fun selectServer(serverId: String) { state = state.copy(selectedServerId = serverId) }
    fun selectApplication(applicationId: String) {
        changeApplication(applicationId)
    }

    private fun changeApplication(applicationId: String?) {
        publicationReadGeneration++
        historyReadGeneration++
        state = state.copy(selectedApplicationId = applicationId, publication = null, publicationHistory = null,
            publicationLookupId = null, publicationLoading = false, historyLoading = false)
        if (applicationId != null) loadHistory(applicationId)
    }

    fun publish(domain: String, contactEmail: String, certificateId: String?, acceptedTerms: Boolean, publiclyReachable: Boolean) {
        val owner = (session.state.value as? SessionState.Active) ?: return
        val serverId = state.selectedServerId ?: return
        val applicationId = state.selectedApplicationId ?: return
        if ((state.applications as? ApiResult.Success)?.value?.none {
                it.id == applicationId && it.actualState.equals("running", true)
            } != false) return
        if (state.publishing) return
        publicationReadGeneration++
        state = state.copy(publishing = true, publication = null, publicationLookupId = null, publicationLoading = false)
        viewModelScope.launch {
            try {
                if (certificateId != null) {
                    val certificate = certificates.certificate(owner, certificateId)
                    if ((session.state.value as? SessionState.Active) !== owner) return@launch
                    if (certificate !is ApiResult.Success || certificate.value.usageProblem(listOf(domain)) != null) {
                        state = state.copy(publishing = false, publication = ApiResult.Problem(409, "webserver.site_certificate_unverified", null)); return@launch
                    }
                }
                val result = webPublishing.publish(WebsitePublishRequest(
                    applicationId = applicationId, webServerId = serverId, domain = domain,
                    certificateId = certificateId, contactEmail = contactEmail, acceptedTerms = acceptedTerms,
                    publicReachabilityConfirmed = publiclyReachable, confirmed = true,
                ), elevationAnswers)
                if ((session.state.value as? SessionState.Active) !== owner) return@launch
                state = state.copy(publishing = false, publication = result,
                    publicationLookupId = (result as? ApiResult.Success)?.value?.operationId)
                if (result is ApiResult.Success) loadHistory(result.value.applicationId)
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if ((session.state.value as? SessionState.Active) === owner) state = state.copy(publication = ApiResult.Transport(null)) }
            finally { if ((session.state.value as? SessionState.Active) === owner) state = state.copy(publishing = false) }
        }
    }

    fun refreshPublication(operationId: String) {
        val owner = (session.state.value as? SessionState.Active) ?: return
        val request = ++publicationReadGeneration
        state = state.copy(publicationLookupId = operationId, publicationLoading = true)
        viewModelScope.launch {
            fun current() = session.state.value === owner && request == publicationReadGeneration
            try {
                val result = webPublishing.operation(owner, operationId)
                if (!current()) return@launch
                state = state.copy(publication = result)
                if (result is ApiResult.Success && result.value.isTerminal()) loadHistory(result.value.applicationId)
            } catch (cancelled: CancellationException) {
                if (current()) state = state.copy(publication = ApiResult.Transport(null))
                throw cancelled
            } catch (_: Exception) {
                if (current()) state = state.copy(publication = ApiResult.Transport(null))
            } finally {
                if (current()) state = state.copy(publicationLoading = false,
                    publicationObservation = state.publicationObservation + 1)
            }
        }
    }

    private fun loadHistory(applicationId: String) {
        val owner = (session.state.value as? SessionState.Active) ?: return
        if (state.selectedApplicationId != applicationId) return
        val request = ++historyReadGeneration
        state = state.copy(publicationHistory = null, historyLoading = true)
        viewModelScope.launch {
            fun current() = session.state.value === owner && request == historyReadGeneration && state.selectedApplicationId == applicationId
            try {
                val result = webPublishing.history(owner, applicationId)
                if (current()) state = state.copy(publicationHistory = result)
            } catch (cancelled: CancellationException) {
                if (current()) state = state.copy(publicationHistory = ApiResult.Transport(null))
                throw cancelled
            } catch (_: Exception) {
                if (current()) state = state.copy(publicationHistory = ApiResult.Transport(null))
            } finally {
                if (current()) state = state.copy(historyLoading = false)
            }
        }
    }

    fun refreshHistory() {
        if (!state.historyLoading) state.selectedApplicationId?.let(::loadHistory)
    }

}

internal fun websitePublicationSelection(current: String?, available: List<String>): String? =
    current?.takeIf { it in available } ?: available.firstOrNull()

internal fun WebsitePublicationOperation.isTerminal(): Boolean = state in setOf("succeeded", "partialFailed", "failed", "cancelled", "interrupted")
