package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

data class WebsitePublicationOutcome(val result: ApiResult<WebsitePublicationOperation>, val storageFailed: Boolean = false)

class WebsitePublicationRequests(private val session: AuthSession, private val repository: WebPublishingRepository,
    private val journal: WebsitePublicationJournal) {
    private val mutations = Mutex()
    fun pending(owner: SessionState.Active): PendingWebsitePublication? { verify(owner); return journal.pending(owner) }
    suspend fun submit(owner: SessionState.Active, request: WebsitePublishRequest, provider: ElevationAnswerProvider): WebsitePublicationOutcome = mutations.withLock {
        verify(owner)
        val existing = journal.pending(owner)
        if (existing != null && !journal.matches(existing, request)) return@withLock WebsitePublicationOutcome(ApiResult.Problem(409, "website.original_request_pending", null))
        if (existing?.operationId != null) return@withLock recoverOriginal(owner, existing, existing.operationId, false)
        if (existing?.attempted == true) {
            // History is read before an explicit same-key retry, but another task never proves ownership of this request.
            val history = repository.history(owner, existing.applicationId); verify(owner)
            if (history !is ApiResult.Success) return@withLock WebsitePublicationOutcome(when (history) {
                is ApiResult.Problem -> history
                else -> ApiResult.Transport(null)
            })
        }
        val pending = journal.begin(owner, request).copy(attempted = true)
        journal.update(pending)
        val result = repository.publish(owner, request, pending.key, provider); verify(owner)
        if (result is ApiResult.Success) return@withLock accepted(owner, pending, result.value)
        // A rejection after an earlier unknown attempt cannot prove that earlier attempt was never accepted.
        if (existing?.attempted != true && result is ApiResult.Problem && result.status in setOf(400, 401, 403, 404, 409, 422)) journal.complete(pending)
        WebsitePublicationOutcome(result)
    }
    suspend fun recover(owner: SessionState.Active, pending: PendingWebsitePublication, id: String, identified: Boolean): WebsitePublicationOutcome = mutations.withLock {
        recoverOriginal(owner, pending, id, identified)
    }
    private suspend fun recoverOriginal(owner: SessionState.Active, pending: PendingWebsitePublication, id: String, identified: Boolean): WebsitePublicationOutcome {
        verify(owner)
        if (journal.pending(owner) != pending || id.isBlank() || pending.operationId?.let { it != id } == true || pending.operationId == null && !identified)
            return WebsitePublicationOutcome(ApiResult.Problem(409, "website.original_request_pending", null))
        val result = repository.operation(owner, id); verify(owner)
        if (result !is ApiResult.Success) return WebsitePublicationOutcome(result)
        if (result.value.applicationId != pending.applicationId || result.value.webServerId != pending.serverId) return WebsitePublicationOutcome(ApiResult.Transport(null))
        return accepted(owner, pending, result.value)
    }
    private fun accepted(owner: SessionState.Active, pending: PendingWebsitePublication, operation: WebsitePublicationOperation): WebsitePublicationOutcome {
        verify(owner)
        return try {
            val recorded = pending.copy(attempted = true, operationId = operation.operationId)
            journal.update(recorded)
            repository.remember(owner, operation)
            journal.complete(recorded)
            WebsitePublicationOutcome(ApiResult.Success(operation))
        } catch (_: Exception) { WebsitePublicationOutcome(ApiResult.Success(operation), storageFailed = true) }
    }
    private fun verify(owner: SessionState.Active) { if (session.state.value !== owner) throw CancellationException("Website publication session changed") }
}
