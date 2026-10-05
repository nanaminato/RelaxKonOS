package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import java.security.MessageDigest
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

class CertificateRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val index: OperationIndex, private val journal: CertificateRequestJournal, private val elevations: ElevationRepository) {
    private val mutations = Mutex()
    fun pending(owner: SessionState.Active): List<PendingCertificateRequest> { verify(owner); return journal.pending(owner) }
    suspend fun list(owner: SessionState.Active) = read(owner) { url, token -> gateway.certificates(url, token) }
    suspend fun certificate(owner: SessionState.Active, id: String): ApiResult<ManagedCertificate> =
        read(owner) { url, token -> gateway.certificate(url, token, id) }.let { result ->
            if (result is ApiResult.Success && result.value.id != InstallationRoutes.canonicalId(id)) ApiResult.Transport(null) else result
        }
    suspend fun kestrel(owner: SessionState.Active, id: String): ApiResult<KestrelCertificateDeployment> =
        read(owner) { url, token -> gateway.kestrelCertificateDeployment(url, token, id) }.let { result ->
            if (result is ApiResult.Success && result.value.certificateId != InstallationRoutes.canonicalId(id)) ApiResult.Transport(null) else result
        }
    suspend fun preflight(owner: SessionState.Active, domains: List<String>, challenge: CertificateChallenge) =
        read(owner) { url, token -> gateway.certificatePreflight(url, token, domains, challenge) }

    suspend fun submit(owner: SessionState.Active, provider: ElevationAnswerProvider, action: CertificateAction, target: String?, body: JsonBody): ApiResult<CertificateOperation> = mutations.withLock {
        verify(owner); require(owner.privilegedOperations && ServerCapabilities.CERTIFICATES in owner.capabilities)
        val id = target?.let(InstallationRoutes::canonicalId)
        require((id == null) == (action in setOf(CertificateAction.Issue, CertificateAction.SelfSigned)))
        val digest = MessageDigest.getInstance("SHA-256").digest(body.toByteArray()).joinToString("") { "%02x".format(it) }
        val pending = try { journal.begin(owner, id, action, digest) }
        catch (_: CertificateOriginalRequestPending) { return@withLock ApiResult.Problem(409, "certificate.original_request_pending", null) }
        if (pending.operationId != null) return@withLock operation(owner, pending.operationId)
        if (pending.attempted) {
            // Explicit retry refreshes facts first. Facts alone never prove which request caused a change.
            val facts = list(owner)
            if (facts !is ApiResult.Success) return@withLock when (facts) { is ApiResult.Problem -> facts; is ApiResult.Transport -> facts; else -> error("Unreachable") }
        }
        if (pending.attempted && action == CertificateAction.DeployKestrel) {
            val facts = kestrel(owner, requireNotNull(id))
            if (facts !is ApiResult.Success) return@withLock when (facts) { is ApiResult.Problem -> facts; is ApiResult.Transport -> facts; else -> error("Unreachable") }
        }
        journal.update(pending.copy(attempted = true))
        val mutation: suspend (String, String) -> ApiResult<CertificateOperation> = { url, token ->
            verify(owner); gateway.certificateMutation(url, token, action, id, body, pending.key)
        }
        val capability = when (action) {
            CertificateAction.Issue -> "certificateIssue"
            CertificateAction.SelfSigned -> "certificateCreateSelfSigned"
            CertificateAction.DeployKestrel -> "certificateReplaceServerHttps"
            CertificateAction.Renew -> "certificateRenew"
            CertificateAction.Revoke -> "certificateRevoke"
            CertificateAction.Delete -> "certificateDelete"
        }
        val target = when (action) {
            CertificateAction.Issue -> "certificates/issue"
            CertificateAction.SelfSigned -> "certificates/self-signed"
            else -> "certificates/${InstallationRoutes.canonicalId(requireNotNull(id))}/${if (action == CertificateAction.DeployKestrel) "server-https" else action.kind}"
        }
        val result = elevations.withElevation(capability, target, provider, mutation).also { verify(owner) }
        if (result is ApiResult.Success) {
            if (result.value.kind != action || result.value.certificateId == null || (id != null && result.value.certificateId != id)) return@withLock ApiResult.Transport(null)
            journal.update(pending.copy(operationId = result.value.operationId, attempted = true))
            record(owner, result.value)
            journal.complete(pending)
        } else if (result is ApiResult.Problem && !pending.attempted && result.status in setOf(400, 401, 403, 409)) journal.complete(pending)
        result
    }
    suspend fun operation(owner: SessionState.Active, id: String): ApiResult<CertificateOperation> =
        read(owner) { url, token -> gateway.certificateOperation(url, token, id) }.let { result ->
            if (result is ApiResult.Success) {
                if (result.value.operationId != InstallationRoutes.canonicalId(id) || result.value.certificateId == null) return@let ApiResult.Transport(null)
                val known = journal.pending(owner).filter { it.operationId == result.value.operationId }
                if (known.any { it.action != result.value.kind || (it.target != null && it.target != result.value.certificateId) }) return@let ApiResult.Transport(null)
                record(owner, result.value)
                journal.pending(owner).filter { it.operationId == result.value.operationId }.forEach(journal::complete)
            }
            result
        }
    suspend fun recover(owner: SessionState.Active, id: String, pending: PendingCertificateRequest? = null): ApiResult<CertificateOperation> = mutations.withLock {
        verify(owner)
        if (pending != null) require(pending in journal.pending(owner))
        val result = operation(owner, id)
        if (result is ApiResult.Success) {
            if (pending != null) {
                if (result.value.kind != pending.action || (pending.target != null && result.value.certificateId != pending.target)) return@withLock ApiResult.Transport(null)
                // The user explicitly identifies the original operation. No content inference is made from SANs.
                journal.complete(pending)
            }
            index.reveal(owner, OperationDomain.Certificate, result.value.operationId)
        }
        result
    }
    suspend fun cancel(owner: SessionState.Active, id: String): ApiResult<CertificateOperation> = mutations.withLock {
        val result = read(owner) { url, token -> gateway.cancelCertificateOperation(url, token, id) }
        if (result is ApiResult.Success) {
            if (result.value.operationId != InstallationRoutes.canonicalId(id) || result.value.certificateId == null) return@withLock ApiResult.Transport(null)
            record(owner, result.value)
        }
        result
    }
    private fun record(owner: SessionState.Active, operation: CertificateOperation) {
        verify(owner); index.record(owner, OperationDomain.Certificate, requireNotNull(operation.certificateId), operation.operationId)
    }
    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner); require(ServerCapabilities.CERTIFICATES in owner.capabilities)
        return session.authenticated { url, token -> verify(owner); call(url, token) }.also { verify(owner) }
    }
    private fun verify(owner: SessionState.Active) { if (session.state.value !== owner) throw CancellationException("Certificate session changed") }
}
