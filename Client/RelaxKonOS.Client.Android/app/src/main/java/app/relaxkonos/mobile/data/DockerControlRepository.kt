package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

class DockerControlRepository(private val gateway: RelaxKonGateway, private val session: AuthSession, private val journal: DockerControlJournal, private val gate: DockerMutationGate) {
    fun pending(owner: SessionState.Active) = journal.pending(owner.also(::verify))
    suspend fun facts(owner: SessionState.Active): ApiResult<DockerControlFacts> {
        val status = read(owner, gateway::dockerStatus); if (status !is ApiResult.Success) return failure(status)
        val mirrors = read(owner, gateway::dockerMirrors); if (mirrors !is ApiResult.Success) return failure(mirrors)
        return ApiResult.Success(DockerControlFacts(status.value, mirrors.value))
    }
    suspend fun change(owner: SessionState.Active, expected: DockerControlFacts, change: DockerControlChange): ApiResult<Unit> = gate.mutex.withLock {
        verify(owner)
        val allowed = gate.check(owner); if (allowed !is ApiResult.Success) return@withLock failure(allowed)
        validate(expected, change)
        val before = facts(owner); if (before !is ApiResult.Success) return@withLock failure(before)
        if (before.value != expected) return@withLock ApiResult.Problem(409, "docker.control.facts_changed", null)
        val marker = journal.begin(owner, change)
        val result = session.authenticated { url, token ->
            verify(owner)
            // A refresh-token retry must reread the approved facts before resending a rejected request.
            val latest = facts(owner); if (latest !is ApiResult.Success) return@authenticated failure<Unit>(latest)
            if (latest.value != expected) return@authenticated ApiResult.Problem(409, "docker.control.facts_changed", null)
            val action = change.engine
            if (action != null) when (val response = gateway.dockerEngineAction(url, token, action, true)) {
                is ApiResult.Success -> if (response.value.success) ApiResult.Success(Unit) else ApiResult.Problem(400, response.value.problemCode, null)
                else -> failure(response)
            } else when (change.kind) {
                DockerControlKind.MirrorCreate -> gateway.dockerCreateMirror(url, token, requireNotNull(change.mirror)).unit()
                DockerControlKind.MirrorUpdate -> gateway.dockerUpdateMirror(url, token, requireNotNull(change.target), requireNotNull(change.mirror)).unit(change.target)
                DockerControlKind.MirrorDelete -> gateway.dockerDeleteMirror(url, token, requireNotNull(change.target))
                DockerControlKind.MirrorSelect -> gateway.dockerSelectMirror(url, token, change.target.takeUnless { it == DockerImageMirror.DEFAULT_ID })
                else -> error("Unexpected Docker change")
            }
        }
        verify(owner)
        if (result is ApiResult.Success && facts(owner) is ApiResult.Success) journal.complete(marker)
        // A transport failure, HTTP problem, or missing post-write facts cannot prove no side effects.
        result
    }
    suspend fun acceptFacts(owner: SessionState.Active, marker: PendingDockerControl): ApiResult<DockerControlFacts> = gate.mutex.withLock {
        verify(owner); require(marker in journal.pending(owner))
        facts(owner).also { if (it is ApiResult.Success) journal.complete(marker) }
    }
    private fun validate(facts: DockerControlFacts, change: DockerControlChange) {
        val action = change.engine
        if (action != null) {
            if (action != DockerEngineAction.Start) require(facts.status.available)
        } else when (change.kind) {
            DockerControlKind.MirrorCreate -> require(DockerMirrorValidation.valid(requireNotNull(change.mirror)))
            DockerControlKind.MirrorUpdate, DockerControlKind.MirrorDelete -> {
                require(facts.mirrors.any { it.id == change.target && !it.default })
                if (change.kind == DockerControlKind.MirrorUpdate) require(DockerMirrorValidation.valid(requireNotNull(change.mirror)))
            }
            DockerControlKind.MirrorSelect -> require(facts.mirrors.any { it.id == change.target })
            else -> error("Unexpected Docker change")
        }
    }
    private suspend fun <T> read(owner: SessionState.Active, request: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner); return session.authenticated { url, token -> verify(owner); request(url, token) }.also { verify(owner) }
    }
    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Docker control session changed")
        require(ServerCapabilities.DOCKER in owner.capabilities)
    }
    private fun ApiResult<DockerImageMirror>.unit(expectedId: String? = null): ApiResult<Unit> = when (this) {
        is ApiResult.Success -> if (!value.default && (expectedId == null || value.id == expectedId)) ApiResult.Success(Unit) else ApiResult.Transport("Mismatched mirror result")
        else -> failure(this)
    }
    private fun <T> failure(result: ApiResult<*>): ApiResult<T> = when (result) {
        is ApiResult.Problem -> result; is ApiResult.Transport -> result; is ApiResult.Success -> error("Expected failed result")
    }
}
