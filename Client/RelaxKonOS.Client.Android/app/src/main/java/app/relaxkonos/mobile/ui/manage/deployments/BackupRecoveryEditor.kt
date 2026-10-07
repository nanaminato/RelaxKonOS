package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.runtime.*
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*

internal data class BackupRecoveryViewState(
    val loading: Boolean = false,
    val manifests: ApiResult<List<BackupManifest>>? = null,
    val preflight: ApiResult<BackupPreflight>? = null,
    val selectedBackupId: String? = null,
    val creating: Boolean = false,
    val creation: ApiResult<BackupManifest>? = null,
    val pendingRequest: Boolean = false,
)


/** Owns backup reads, reconciliation and writes for one application and exact session owner. */
internal class BackupRecoveryEditor(
    private val container: AppContainer,
    private val owner: SessionState.Active,
    private val applicationId: String,
    parentScope: CoroutineScope,
) {
    private val job = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + job)
    private var preflightJob: Job? = null
    private val current get() = job.isActive && container.session.state.value === owner
    var state by mutableStateOf(BackupRecoveryViewState())
        private set

    fun close() { job.cancel() }

    fun load() {
        if (!current || state.loading || state.creating) return
        preflightJob?.cancel()
        state = BackupRecoveryViewState(loading = true)
        scope.launch {
            try {
                val result = withContext(Dispatchers.IO) { container.backupRecovery.manifests(owner, applicationId) }
                if (!current) return@launch
                val reconciled = withContext(Dispatchers.IO) { container.backupRecovery.reconcileDefinitionBackup(owner, applicationId) }
                if (current) {
                    val manifest = (reconciled as? ApiResult.Success)?.value
                    val merged = if (result is ApiResult.Success && manifest != null)
                        ApiResult.Success((listOf(manifest) + result.value).distinctBy { it.backupId }) else result
                    state = state.copy(loading = false, manifests = merged,
                        pendingRequest = container.backupRecovery.hasPendingDefinitionBackup(owner, applicationId))
                }
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current) state = state.copy(loading = false, manifests = ApiResult.Transport(null)) }
        }
    }

    fun create() {
        if (!current || state.loading || state.creating) return
        state = state.copy(creating = true, creation = null)
        scope.launch {
            try {
                val result = withContext(Dispatchers.IO) { container.backupRecovery.createDefinitionBackup(owner, applicationId) }
                if (current) {
                    val manifests = state.manifests
                    val merged = if (result is ApiResult.Success && manifests is ApiResult.Success)
                        ApiResult.Success((listOf(result.value) + manifests.value).distinctBy { it.backupId }) else manifests
                    state = state.copy(creating = false, creation = result, manifests = merged,
                        pendingRequest = container.backupRecovery.hasPendingDefinitionBackup(owner, applicationId))
                }
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current) state = state.copy(creating = false, creation = ApiResult.Transport(null),
                pendingRequest = container.backupRecovery.hasPendingDefinitionBackup(owner, applicationId)) }
        }
    }

    fun select(backupId: String) {
        if (!current || state.loading || state.creating) return
        preflightJob?.cancel()
        state = state.copy(selectedBackupId = backupId, preflight = null)
        preflightJob = scope.launch {
            val result = try { withContext(Dispatchers.IO) { container.backupRecovery.preflight(owner, backupId) } }
                catch (cancelled: CancellationException) { throw cancelled }
                catch (_: Exception) { ApiResult.Transport(null) }
            if (current && state.selectedBackupId == backupId) state = state.copy(preflight = result)
        }
    }
}
