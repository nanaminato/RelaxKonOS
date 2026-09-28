package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** No file content or credentials are persisted on the phone. Every call stays in one login session. */
class GitRepositoryClient(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    private val calls = Mutex()

    suspend fun repositories(owner: SessionState.Active) = call(owner) { url, token -> gateway.gitRepositories(url, token) }
    suspend fun register(owner: SessionState.Active, name: String, path: String) =
        call(owner) { url, token -> gateway.gitRegisterRepository(url, token, name, path) }
    suspend fun branches(owner: SessionState.Active, id: String) = call(owner) { url, token -> gateway.gitBranches(url, token, id) }
    suspend fun status(owner: SessionState.Active, id: String) = call(owner) { url, token -> gateway.gitStatus(url, token, id) }
    suspend fun textFile(owner: SessionState.Active, id: String, path: String) =
        call(owner) { url, token -> gateway.gitTextFile(url, token, id, path) }
    suspend fun save(owner: SessionState.Active, id: String, baseline: GitTextFile, content: String) =
        call(owner) { url, token -> gateway.gitSaveTextFile(url, token, id, baseline, content) }
    suspend fun commit(owner: SessionState.Active, id: String, path: String, message: String) =
        call(owner) { url, token -> gateway.gitCommit(url, token, id, path, message) }
    suspend fun push(owner: SessionState.Active, id: String) = call(owner) { url, token -> gateway.gitPush(url, token, id) }
    suspend fun credentials(owner: SessionState.Active) = call(owner) { url, token -> gateway.gitBuildCredentials(url, token) }
    suspend fun setCredential(owner: SessionState.Active, name: String, tokenValue: String) =
        call(owner) { url, token -> gateway.gitBuildSetCredential(url, token, name, tokenValue) }
    suspend fun resolve(owner: SessionState.Active, url: String, reference: String, credentialId: String?) =
        call(owner) { server, token -> gateway.gitBuildResolve(server, token, url, reference, credentialId) }
    suspend fun refs(owner: SessionState.Active, url: String, credentialId: String?) =
        call(owner) { server, token -> gateway.gitBuildRefs(server, token, url, credentialId) }
    suspend fun builds(owner: SessionState.Active) = call(owner) { url, token -> gateway.gitBuilds(url, token) }
    suspend fun startBuild(owner: SessionState.Active, request: GitBuildRequest, key: String) =
        call(owner) { url, token -> gateway.gitBuildStart(url, token, request, key) }
    suspend fun build(owner: SessionState.Active, id: String) = call(owner) { url, token -> gateway.gitBuildGet(url, token, id) }
    suspend fun cancelBuild(owner: SessionState.Active, id: String) = call(owner) { url, token -> gateway.gitBuildCancel(url, token, id) }

    private suspend fun <T> call(owner: SessionState.Active, action: suspend (String, String) -> ApiResult<T>): ApiResult<T> = calls.withLock {
        fun verify() { if (session.state.value !== owner) throw CancellationException("Git session changed") }
        verify()
        val result = session.authenticated { url, token -> verify(); action(url, token) }
        verify()
        result
    }
}
