package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.runtime.*
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.data.DeploymentRepository
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import java.util.UUID

internal class DeploymentDefinitionEditor(
    private val session: AuthSession,
    private val deployments: DeploymentRepository,
    private val owner: SessionState.Active,
    private val baseline: DeploymentApplication,
    parentScope: CoroutineScope,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
) {
    private val job = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + job)
    fun close() { job.cancel() }
    var draft by mutableStateOf(DeploymentDefinitionDraft(baseline))
    var busy by mutableStateOf(false)
    var result by mutableStateOf<ApiResult<DeploymentApplication>?>(null)
    var feedbackVersion by mutableStateOf(0L)
        private set
    var unknown by mutableStateOf(false)
    var loadedCurrent by mutableStateOf(false)
    var preview by mutableStateOf(false)
    var pending by mutableStateOf(false)
    var configName by mutableStateOf("")
    var configValue by mutableStateOf("")
    var configSecret by mutableStateOf(false)
    var volumeName by mutableStateOf("")
    var volumePath by mutableStateOf("")
    var volumeReadOnly by mutableStateOf(false)
    var entryInvalid by mutableStateOf(false)
    val saved get() = result is ApiResult.Success
    val editable get() = !busy && !saved && !unknown && !pending
    val unstaged get() = configName.isNotEmpty() || configValue.isNotEmpty() || volumeName.isNotEmpty() || volumePath.isNotEmpty()
    val dirty get() = draft.changed || unstaged || configSecret || volumeReadOnly
    val request get() = draft.requestOrNull()

    fun loadCurrent() {
        if (busy) return
        busy = true
        scope.launch {
            try {
                val current = withContext(ioDispatcher) { deployments.snapshot(owner, baseline.id) }
                if (session.state.value !== owner) return@launch
                when (current) {
                    is ApiResult.Success -> {
                        if (current.value.application.id != baseline.id) { result = ApiResult.Transport(null); return@launch }
                        draft = DeploymentDefinitionDraft(current.value.application)
                        pending = current.value.activeOperation != null
                        result = null; unknown = false; preview = false; loadedCurrent = true
                        configName = ""; configValue = ""; configSecret = false
                        volumeName = ""; volumePath = ""; volumeReadOnly = false; entryInvalid = false
                    }
                    is ApiResult.Problem -> result = current
                    is ApiResult.Transport -> result = current
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                if (session.state.value === owner) result = ApiResult.Transport(null)
            } finally {
                if (session.state.value === owner && result != null) feedbackVersion++
                busy = false
            }
        }
    }

    fun save(onSaved: () -> Unit) {
        if (!editable || unstaged) return

        val submitted = draft.requestOrNull() ?: return
        feedbackVersion++
        busy = true; result = null; loadedCurrent = false; configValue = ""
        scope.launch {
            try {
                val outcome = withContext(ioDispatcher) { deployments.saveDefinition(owner, draft.baseline, submitted, UUID.randomUUID().toString()) }
                if (session.state.value !== owner) return@launch
                result = outcome.result; unknown = outcome.mayHaveSaved
                if (outcome.result is ApiResult.Success) { draft = DeploymentDefinitionDraft(outcome.result.value); onSaved() }
            } finally { busy = false }
        }

    }
}
