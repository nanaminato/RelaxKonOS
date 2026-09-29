package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.BackupManifest
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Server-owned backup manifests; no backup key, plaintext or request body is retained on Android. */
class BackupRecoveryRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
    private val operationIndex: OperationIndex,
    private val requestsJournal: BackupRecoveryRequestJournal,
) {
    private val requests = Mutex()
    suspend fun manifests(owner: SessionState.Active, applicationId: String): ApiResult<List<BackupManifest>> =
        call(owner) { url, token -> gateway.backupManifests(url, token, applicationId) }.also { result ->
            if (result is ApiResult.Success) result.value.forEach { manifest ->
                runCatching { operationIndex.record(owner, OperationDomain.Backup, applicationId, manifest.backupId) }
            }
        }
    suspend fun manifest(owner: SessionState.Active, backupId: String) = call(owner) { url, token -> gateway.backupManifest(url, token, backupId) }
    suspend fun preflight(owner: SessionState.Active, backupId: String) = call(owner) { url, token -> gateway.backupPreflight(url, token, backupId) }

    /** Starts a new backup or repeats the exact persisted key after an inconclusive HTTP result. */
    suspend fun createDefinitionBackup(owner: SessionState.Active, applicationId: String): ApiResult<BackupManifest> {
        val pending = requestsJournal.begin(owner, applicationId)
        return call(owner) { url, token -> gateway.createDefinitionBackup(url, token, applicationId, pending.idempotencyKey) }
            .also { result -> complete(owner, applicationId, pending.idempotencyKey, result) }
    }

    /** A read-only reconciliation; a missing response is never interpreted as a failed remote write. */
    suspend fun reconcileDefinitionBackup(owner: SessionState.Active, applicationId: String): ApiResult<BackupManifest?> {
        val pending = requestsJournal.pending(owner, applicationId) ?: return ApiResult.Success(null)
        return call(owner) { url, token -> gateway.definitionBackupRequest(url, token, applicationId, pending.idempotencyKey) }
            .also { result -> if (result is ApiResult.Success && result.value != null) {
                complete(owner, applicationId, pending.idempotencyKey, ApiResult.Success(result.value))
            } }
    }

    fun hasPendingDefinitionBackup(owner: SessionState.Active, applicationId: String): Boolean =
        requestsJournal.pending(owner, applicationId) != null

    private fun complete(owner: SessionState.Active, applicationId: String, key: String, result: ApiResult<BackupManifest>) {
        if (result !is ApiResult.Success) return
        requestsJournal.complete(owner, applicationId, key)
        runCatching { operationIndex.record(owner, OperationDomain.Backup, applicationId, result.value.backupId) }
    }

    private suspend fun <T> call(owner: SessionState.Active, request: suspend (String, String) -> ApiResult<T>): ApiResult<T> = requests.withLock {
        fun verify() { if (session.state.value !== owner) throw CancellationException("Backup recovery session changed") }
        verify()
        val result = session.authenticated { url, token -> verify(); request(url, token) }
        verify()
        result
    }
}
