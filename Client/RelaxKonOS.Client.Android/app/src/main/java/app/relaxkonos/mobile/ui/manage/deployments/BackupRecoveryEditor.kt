package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.runtime.*
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.data.BackupRecoveryRepository
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*

internal data class BackupRecoveryViewState(
    val loading: Boolean = false,
    val manifests: ApiResult<List<BackupManifest>>? = null,
    val reconciliation: ApiResult<BackupManifest?>? = null,
    val preflight: ApiResult<BackupPreflight>? = null,
    val selectedBackupId: String? = null,
    val creating: Boolean = false,
    val creation: ApiResult<BackupManifest>? = null,
    val pendingRequest: Boolean = false,
)


/** Owns backup reads, reconciliation and writes for one application and exact session owner. */
internal class BackupRecoveryEditor(
    private val session: AuthSession,
    private val backups: BackupRecoveryRepository,
    private val owner: SessionState.Active,
    private val applicationId: String,
    parentScope: CoroutineScope,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
) {
    private val job = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + job)
    private var preflightJob: Job? = null
    private val current get() = job.isActive && session.state.value === owner
    var state by mutableStateOf(BackupRecoveryViewState())
        private set
    var creationFeedbackVersion by mutableLongStateOf(0L)
        private set
    var preflightFeedbackVersion by mutableLongStateOf(0L)
        private set

    fun close() { job.cancel() }

    fun load() {
        if (!current || state.loading || state.creating) return
        preflightJob?.cancel()
        state = BackupRecoveryViewState(loading = true,
            pendingRequest = backups.hasPendingDefinitionBackup(owner, applicationId))
        scope.launch {
            val result = readSafely { backups.manifests(owner, applicationId) }
            if (!current) return@launch
            state = state.copy(manifests = result)
            val reconciled = readSafely { backups.reconcileDefinitionBackup(owner, applicationId) }
            if (current) {
                val manifest = (reconciled as? ApiResult.Success)?.value
                val merged = if (result is ApiResult.Success && manifest != null)
                    ApiResult.Success((listOf(manifest) + result.value).distinctBy { it.backupId }) else result
                state = state.copy(loading = false, manifests = merged, reconciliation = reconciled,
                    pendingRequest = backups.hasPendingDefinitionBackup(owner, applicationId))
            }
        }
    }

    private suspend fun <T> readSafely(read: suspend () -> ApiResult<T>): ApiResult<T> =
        try { withContext(ioDispatcher) { read() } }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { ApiResult.Transport(null) }

    fun create() {
        if (!current || state.loading || state.creating) return
        creationFeedbackVersion++
        state = state.copy(creating = true, creation = null)
        scope.launch {
            try {
                val result = withContext(ioDispatcher) { backups.createDefinitionBackup(owner, applicationId) }
                if (current) {
                    val manifests = state.manifests
                    val merged = if (result is ApiResult.Success && manifests is ApiResult.Success)
                        ApiResult.Success((listOf(result.value) + manifests.value).distinctBy { it.backupId }) else manifests
                    state = state.copy(creating = false, creation = result, manifests = merged,
                        reconciliation = if (result is ApiResult.Success) null else state.reconciliation,
                        pendingRequest = backups.hasPendingDefinitionBackup(owner, applicationId))
                }
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current) state = state.copy(creating = false, creation = ApiResult.Transport(null),
                pendingRequest = backups.hasPendingDefinitionBackup(owner, applicationId)) }
        }
    }

    fun select(backupId: String) {
        if (!current || state.loading || state.creating) return
        preflightJob?.cancel()
        preflightFeedbackVersion++
        state = state.copy(selectedBackupId = backupId, preflight = null)
        preflightJob = scope.launch {
            val result = try { withContext(ioDispatcher) { backups.preflight(owner, backupId) } }
                catch (cancelled: CancellationException) { throw cancelled }
                catch (_: Exception) { ApiResult.Transport(null) }
            if (current && state.selectedBackupId == backupId) state = state.copy(preflight = result)
        }
    }
}
