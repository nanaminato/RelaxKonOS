package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import java.security.MessageDigest
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Keep this exact intent for explicit retries; the journal can recover its key after process death. */
class InstallationSubmission internal constructor(
    internal val owner: SessionState.Active, val kind: InstallationKind, val request: InstallationRequest,
    internal val pending: PendingInstallationRequest,
)

class InstallationRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
    private val elevations: ElevationRepository,
    private val index: OperationIndex,
    private val journal: InstallationRequestJournal,
) {
    private val mutations = Mutex()

    fun prepare(owner: SessionState.Active, kind: InstallationKind, request: InstallationRequest): InstallationSubmission {
        verify(owner)
        require(request.confirmed)
        require(owner.privilegedOperations && request.service.capability in owner.capabilities)
        val digest = MessageDigest.getInstance("SHA-256").digest(InstallationWire.request(request, kind).toByteArray())
            .joinToString("") { "%02x".format(it) }
        return InstallationSubmission(owner, kind, request, journal.begin(owner, request.service, kind, digest))
    }

    fun pending(owner: SessionState.Active): List<PendingInstallationRequest> { verify(owner); return journal.pending(owner) }

    /** No transport retry. Reinvocation is an explicit retry with the original key and options. */
    suspend fun submit(intent: InstallationSubmission, provider: ElevationAnswerProvider): ApiResult<InstallationOperation> = mutations.withLock {
        val owner = intent.owner
        verify(owner)
        val saved = journal.pending(owner).firstOrNull { it.key == intent.pending.key }
        NginxDiagnostics.event("install.submit service=${intent.request.service} kind=${intent.kind} saved=${saved != null} attempted=${saved?.attempted} knownId=${saved?.operationId}")
        val knownId = saved?.operationId
        if (saved != null && knownId != null) {
            return@withLock operation(owner, knownId).also { if (it is ApiResult.Success) journal.complete(saved) }
        }
        if (saved?.attempted == true) {
            // Query facts before any explicit replay. An active operation is not proof of this key's ownership.
            when (val active = active(owner, intent.request.service)) {
                is ApiResult.Problem -> return@withLock active
                is ApiResult.Transport -> return@withLock active
                is ApiResult.Success -> Unit
            }
        }
        if (saved != null) journal.update(saved.copy(attempted = true))
        val guardedProvider = ElevationAnswerProvider { capability, target ->
            verify(owner)
            val answer = provider.answer(capability, target)
            if (session.state.value !== owner) {
                answer?.password?.fill('\u0000')
                verify(owner)
            }
            answer
        }
        val result = elevations.withElevation(intent.request.service.elevation, intent.request.service.wire, guardedProvider) { url, token ->
            verify(owner)
            gateway.startInstallation(url, token, intent.kind, intent.request, intent.pending.key)
        }
        NginxDiagnostics.event("install.response ${NginxDiagnostics.result(result)} operation=${(result as? ApiResult.Success)?.value?.operationId} state=${(result as? ApiResult.Success)?.value?.state}")
        verify(owner)
        when (result) {
            is ApiResult.Success -> {
                if (result.value.service != intent.request.service || result.value.kind != intent.kind)
                    return@withLock ApiResult.Transport("Installation response does not match the request.")
                // Persist the ID before dropping the unresolved request. Failed index writes retain recovery evidence.
                if (saved != null) journal.update(saved.copy(attempted = true, operationId = result.value.operationId))
                record(owner, result.value)
                if (saved != null) journal.complete(saved)
            }
            is ApiResult.Problem -> {
                // Only a never-ambiguous explicit preflight refusal proves nothing was queued.
                if (saved != null && !saved.attempted && result.status in setOf(400, 401, 403)) journal.complete(saved)
            }
            is ApiResult.Transport -> Unit
        }
        result
    }

    suspend fun operation(owner: SessionState.Active, id: String): ApiResult<InstallationOperation> {
        return read(owner) { url, token -> gateway.installation(url, token, id) }.also { result ->
            if (result is ApiResult.Success) {
                if (result.value.operationId != InstallationRoutes.canonicalId(id)) return ApiResult.Transport("Installation ID mismatch.")
                record(owner, result.value)
            }
        }
    }

    /** Explicit user lookup also restores a local hidden record; ordinary polling never does. */
    suspend fun recoverById(owner: SessionState.Active, id: String): ApiResult<InstallationOperation> {
        val result = operation(owner, id)
        if (result is ApiResult.Success) {
            verify(owner)
            index.reveal(owner, OperationDomain.Installation, result.value.operationId)
            journal.pending(owner).filter { it.operationId == result.value.operationId }.forEach(journal::complete)
        }
        return result
    }

    /** User explicitly identifies this pending request with its original operation ID; facts alone never establish ownership. */
    suspend fun identifyOriginal(owner: SessionState.Active, pending: PendingInstallationRequest, id: String): ApiResult<InstallationOperation> = mutations.withLock {
        verify(owner); require(pending in journal.pending(owner))
        val result = operation(owner, id)
        if (result is ApiResult.Success) {
            if (result.value.service != pending.service || result.value.kind != pending.kind || (pending.operationId != null && pending.operationId != result.value.operationId))
                return@withLock ApiResult.Transport("Installation ownership mismatch.")
            index.reveal(owner, OperationDomain.Installation, result.value.operationId)
            journal.complete(pending)
        }
        result
    }

    suspend fun active(owner: SessionState.Active, service: InstallationService): ApiResult<InstallationOperation?> {
        return read(owner) { url, token -> gateway.activeInstallation(url, token, service) }.let { result ->
            if (result is ApiResult.Success && result.value != null) {
                if (result.value.service != service || !result.value.state.active) return ApiResult.Transport("Invalid active installation response.")
                record(owner, result.value)
            }
            result
        }
    }

    /** The returned operation may still be running. Callers must query it until a real terminal state. */
    suspend fun cancel(owner: SessionState.Active, id: String): ApiResult<InstallationOperation> = mutations.withLock {
        val operationId = InstallationRoutes.canonicalId(id)
        val key = UUID.nameUUIDFromBytes("installation-cancel:${owner.serviceId}:${owner.userName}:$operationId".toByteArray(Charsets.UTF_8)).toString()
        read(owner) { url, token -> gateway.cancelInstallation(url, token, operationId, key) }.let { result ->
            if (result is ApiResult.Success) {
                if (result.value.operationId != InstallationRoutes.canonicalId(id)) return@withLock ApiResult.Transport("Installation ID mismatch.")
                record(owner, result.value)
            }
            result
        }
    }

    suspend fun fileReference(owner: SessionState.Active, service: InstallationService, path: String): ApiResult<InstallationFileReference> {
        require(service.acceptsPackage && path.isNotBlank())
        return read(owner) { url, token -> gateway.installationFileReference(url, token, service, path) }
    }

    /** PickedDocument reopens its stream for the single authorized 401 retry. No full package in memory. */
    suspend fun upload(owner: SessionState.Active, service: InstallationService, document: PickedDocument,
        onProgress: ((Long) -> Unit)? = null): ApiResult<InstallationFileReference> {
        require(service.acceptsPackage)
        return read(owner) { url, token -> gateway.uploadInstallationPackage(url, token, service,
            document.displayName, document.length, { verify(owner); document.open() },
            { written -> verify(owner); onProgress?.invoke(written) }) }
    }

    /** A known ID always takes precedence; otherwise only active discovery is possible on this API. */
    suspend fun recover(owner: SessionState.Active, pending: PendingInstallationRequest): ApiResult<InstallationOperation?> {
        verify(owner)
        require(pending.serviceId == owner.serviceId && pending.account == owner.userName)
        val id = pending.operationId ?: return active(owner, pending.service)
        val result = operation(owner, id)
        if (result is ApiResult.Success) journal.complete(pending)
        return result
    }

    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner)
        val result = session.authenticated { url, token -> verify(owner); call(url, token) }
        verify(owner)
        return result
    }
    private fun record(owner: SessionState.Active, operation: InstallationOperation) {
        verify(owner)
        index.record(owner, OperationDomain.Installation, operation.service.name, operation.operationId)
    }
    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Installation session changed")
    }
}
