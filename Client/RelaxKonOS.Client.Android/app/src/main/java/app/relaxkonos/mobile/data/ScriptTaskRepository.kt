package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.core.net.ScriptRequest
import app.relaxkonos.mobile.core.net.ScriptTaskResult
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

class ScriptTaskRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
    private val operationIndex: OperationIndex,
) {
    private val requests = Mutex()

    suspend fun tasks(owner: SessionState.Active) = call(owner) { url, token -> gateway.scriptTasks(url, token) }
    suspend fun task(owner: SessionState.Active, id: String) = call(owner) { url, token -> gateway.scriptTask(url, token, id) }
    suspend fun submit(owner: SessionState.Active, request: ScriptRequest, key: String) =
        call(owner) { url, token -> gateway.scriptSubmit(url, token, request, key) }
    suspend fun cancel(owner: SessionState.Active, id: String) = call(owner) { url, token -> gateway.scriptCancel(url, token, id) }

    private suspend fun <T> call(owner: SessionState.Active, request: suspend (String, String) -> ApiResult<T>): ApiResult<T> =
        requests.withLock {
            fun verify() { if (session.state.value !== owner) throw CancellationException("Script session changed") }
            verify()
            val result = session.authenticated { url, token -> verify(); request(url, token) }
            verify()
            val task = ((result as? ApiResult.Success)?.value as? ScriptTaskResult)?.task
            if (task != null) runCatching { operationIndex.record(owner, OperationDomain.Script, task.id, task.id) }
            result
        }
}
