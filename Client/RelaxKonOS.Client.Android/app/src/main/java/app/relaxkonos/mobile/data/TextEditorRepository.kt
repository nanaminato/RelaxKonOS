package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

class TextEditorRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    private val calls = Mutex()
    suspend fun read(owner: SessionState.Active, path: String, repositoryId: String?) = call(owner) { url, token ->
        validate(if (repositoryId == null) gateway.textFile(url, token, path) else gateway.gitTextFile(url, token, repositoryId, path), path)
    }
    suspend fun save(owner: SessionState.Active, file: RemoteTextFile, content: String, repositoryId: String?) = call(owner) { url, token ->
        require(TextEditorPolicy.valid(content, file.encoding, file.bom))
        validate(if (repositoryId == null) gateway.saveTextFile(url, token, file, content) else gateway.gitSaveTextFile(url, token, repositoryId, file, content), file.path, content, file.encoding, file.bom)
    }
    suspend fun create(owner: SessionState.Active, path: String, content: String, encoding: String, bom: Boolean) = call(owner) { url, token ->
        require(TextEditorPolicy.valid(content, encoding, bom))
        validate(gateway.createTextFile(url, token, path, content, encoding, bom), path, content, encoding, bom)
    }
    private fun validate(result: ApiResult<RemoteTextFile>, path: String, content: String? = null, encoding: String? = null, bom: Boolean? = null): ApiResult<RemoteTextFile> {
        val file = (result as? ApiResult.Success)?.value ?: return result
        return if (file.path != path || content != null && file.content != content || encoding != null && file.encoding != encoding || bom != null && file.bom != bom)
            ApiResult.Transport("Unexpected text file receipt.") else result
    }
    private suspend fun <T> call(owner: SessionState.Active, action: suspend (String, String) -> ApiResult<T>): ApiResult<T> = calls.withLock {
        fun verify() { if (session.state.value !== owner) throw CancellationException("Editor session changed") }
        verify()
        val result = session.authenticated { url, token -> verify(); action(url, token) }
        verify()
        result
    }
}
