package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.runtime.*
import app.relaxkonos.mobile.AppContainer
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import java.util.UUID

internal class DeploymentRevisionEditor(
    private val container: AppContainer,
    private val owner: SessionState.Active,
    initial: DeploymentSnapshot,
    parentScope: CoroutineScope,
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
    var unknown by mutableStateOf(container.deployments.hasUncertainRevision(owner, baseline.id))
    var result by mutableStateOf<ApiResult<DeploymentOperation>?>(null)
    var picker by mutableStateOf(false)
    fun reload(onClearArchive: () -> Unit) {
        busy = true
        scope.launch {
            try {
                val current = withContext(Dispatchers.IO) { container.deployments.reconcileRevision(owner, baseline.id) }
                if (container.session.state.value !== owner) return@launch
                when (current) {
                    is ApiResult.Success -> if (current.value.application.id == baseline.id) {
                        snapshot = current.value; preview = false; result = null
                        unknown = container.deployments.hasUncertainRevision(owner, baseline.id)
                        image = ""; baseImage = ""; runtime = ""; entry = ""; arguments.clear(); selfContained = false
                        onClearArchive()
                    } else result = ApiResult.Transport(null)
                    is ApiResult.Problem -> result = current
                    is ApiResult.Transport -> result = current
                }
            } finally { busy = false }
        }
    }

    fun submit(source: DeploymentRevisionSource, onClearArchive: () -> Unit, onAccepted: (DeploymentOperation) -> Unit) {
        if (busy) return
        busy = true; result = null
        scope.launch {
            try {
                val submitted = withContext(Dispatchers.IO) { container.deployments.deployRevision(owner, baseline, source, UUID.randomUUID().toString()) }
                if (container.session.state.value !== owner) return@launch
                result = submitted.result; unknown = submitted.mayHaveQueued
                if (submitted.result is ApiResult.Success) { onClearArchive(); onAccepted(submitted.result.value) }
            } finally { busy = false }
        }

    }
}
