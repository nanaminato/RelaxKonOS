package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.runtime.*
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.data.DeploymentRepository
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import java.util.UUID

internal class DeploymentRevisionEditor(
    private val session: AuthSession,
    private val deployments: DeploymentRepository,
    private val owner: SessionState.Active,
    initial: DeploymentSnapshot,
    parentScope: CoroutineScope,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
) {
    private val job = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + job)
    fun close() { job.cancel() }
    var snapshot by mutableStateOf(initial)
    val baseline get() = snapshot.application
    var image by mutableStateOf("")
    var baseImage by mutableStateOf("")
    var runtime by mutableStateOf("")
    var entry by mutableStateOf("")
    var selfContained by mutableStateOf(false)
    val arguments = mutableStateListOf<String>()
    var busy by mutableStateOf(false)
    var preview by mutableStateOf(false)
    var unknown by mutableStateOf(deployments.hasUncertainRevision(owner, baseline.id))
    var result by mutableStateOf<ApiResult<DeploymentOperation>?>(null)
    var feedbackVersion by mutableLongStateOf(0L)
        private set
    var picker by mutableStateOf(false)
    fun reload(onClearArchive: () -> Unit) {
        if (busy) return
        busy = true
        scope.launch {
            try {
                val current = withContext(ioDispatcher) { deployments.reconcileRevision(owner, baseline.id) }
                if (session.state.value !== owner) return@launch
                when (current) {
                    is ApiResult.Success -> if (current.value.application.id == baseline.id) {
                        snapshot = current.value; preview = false; result = null
                        unknown = deployments.hasUncertainRevision(owner, baseline.id)
                        image = ""; baseImage = ""; runtime = ""; entry = ""; arguments.clear(); selfContained = false
                        onClearArchive()
                    } else result = ApiResult.Transport(null)
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

    fun submit(source: DeploymentRevisionSource, onClearArchive: () -> Unit, onAccepted: (DeploymentOperation) -> Unit) {
        if (busy || unknown || result is ApiResult.Success || snapshot.activeOperation != null) return
        feedbackVersion++
        busy = true; result = null
        scope.launch {
            try {
                val submitted = withContext(ioDispatcher) { deployments.deployRevision(owner, baseline, source, UUID.randomUUID().toString()) }
                if (session.state.value !== owner) return@launch
                result = submitted.result; unknown = submitted.mayHaveQueued
                if (submitted.result is ApiResult.Success) { onClearArchive(); onAccepted(submitted.result.value) }
            } finally { busy = false }
        }

    }
}
