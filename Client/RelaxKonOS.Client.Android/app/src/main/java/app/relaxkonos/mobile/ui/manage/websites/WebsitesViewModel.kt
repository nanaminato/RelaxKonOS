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

internal class WebsitePublicationReview(val owner: SessionState.Active, val pending: PendingWebsitePublication)

internal data class WebsitesState(
    val pendingPublication: WebsitePublicationReview? = null,
    val publicationStorageFailed: Boolean = false,
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
    private val elevationAnswers: ElevationAnswerProvider, publicationJournal: WebsitePublicationJournal) : ViewModel() {
    private val publicationRequests = WebsitePublicationRequests(session, webPublishing, publicationJournal)

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
        if (state.loading || state.publishing) return
        refreshPendingPublication(owner)
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

    private fun refreshPendingPublication(owner: SessionState.Active, storageFailed: Boolean = false): Boolean {
        return try {
            val pending = publicationRequests.pending(owner)
            state = state.copy(pendingPublication = pending?.let { WebsitePublicationReview(owner, it) }, publicationStorageFailed = storageFailed)
            true
        } catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) {
            if (session.state.value === owner) state = state.copy(publicationStorageFailed = true)
            false
        }
    }

    private fun refreshFailed(owner: SessionState.Active) {
        if (session.state.value === owner) state = state.copy(servers = ApiResult.Transport(null),
            certificates = null, applications = null)
    }

    fun selectServer(serverId: String) {
        if (state.publishing || state.loading) return
        state = state.copy(selectedServerId = serverId)
    }
    fun selectApplication(applicationId: String) {
        if (state.publishing) return
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
        if (state.publishing || state.loading) return
        if (!refreshPendingPublication(owner)) return
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
                val outcome = publicationRequests.submit(owner, WebsitePublishRequest(
                    applicationId = applicationId, webServerId = serverId, domain = domain,
                    certificateId = certificateId, contactEmail = contactEmail, acceptedTerms = acceptedTerms,
                    publicReachabilityConfirmed = publiclyReachable, confirmed = true,
                ), elevationAnswers)
                if ((session.state.value as? SessionState.Active) !== owner) return@launch
                val result = outcome.result
                state = state.copy(publishing = false, publication = result,
                    publicationLookupId = (result as? ApiResult.Success)?.value?.operationId)
                refreshPendingPublication(owner, outcome.storageFailed)
                if (result is ApiResult.Success) loadHistory(result.value.applicationId)
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: WebsitePublicationStorageException) {
                if (session.state.value === owner) state = state.copy(publicationStorageFailed = true)
            }
            catch (_: Exception) { if ((session.state.value as? SessionState.Active) === owner) state = state.copy(publication = ApiResult.Transport(null)) }
            finally {
                if (session.state.value === owner) {
                    state = state.copy(publishing = false)
                    refreshPendingPublication(owner, state.publicationStorageFailed)
                }
            }
        }
    }

    fun recoverPendingPublication(review: WebsitePublicationReview, id: String) {
        val owner = session.state.value as? SessionState.Active ?: return
        if (state.publishing || state.loading || state.pendingPublication !== review || review.owner !== owner || id.isBlank()) return
        publicationReadGeneration++
        state = state.copy(publishing = true, publicationLoading = false, publication = null, publicationLookupId = id.trim())
        viewModelScope.launch {
            try {
                val outcome = publicationRequests.recover(owner, review.pending, id.trim(), identified = true)
                if (session.state.value !== owner) return@launch
                state = state.copy(publication = outcome.result,
                    publicationLookupId = (outcome.result as? ApiResult.Success)?.value?.operationId ?: id.trim())
                refreshPendingPublication(owner, outcome.storageFailed)
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (session.state.value === owner) state = state.copy(publication = ApiResult.Transport(null)) }
            finally {
                if (session.state.value === owner) {
                    state = state.copy(publishing = false)
                    refreshPendingPublication(owner, state.publicationStorageFailed)
                }
            }
        }
    }

    fun refreshPublication(operationId: String) {
        if (state.publishing) return
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
        if (!state.historyLoading && !state.publishing) state.selectedApplicationId?.let(::loadHistory)
    }

}

internal fun websitePublicationSelection(current: String?, available: List<String>): String? =
    current?.takeIf { it in available } ?: available.firstOrNull()

internal fun WebsitePublicationOperation.isTerminal(): Boolean = state in setOf("succeeded", "partialFailed", "failed", "cancelled", "interrupted")
