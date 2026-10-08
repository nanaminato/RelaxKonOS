package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Serial current-contract calls. Every call and refresh retry is bound to the original login. */
class GuardianRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    private val pipe = Mutex()
    private val mutations = Mutex()
    suspend fun status(owner: SessionState.Active) = owned(owner) { url, token -> gateway.guardianStatus(url, token) }
    suspend fun workloads(owner: SessionState.Active) = owned(owner) { url, token -> gateway.guardianWorkloads(url, token) }
    suspend fun logs(owner: SessionState.Active, id: String) = owned(owner) { url, token -> gateway.guardianLogs(url, token, id) }
    suspend fun definition(owner: SessionState.Active, id: String): ApiResult<GuardianDefinition> = owned(owner) { url, token ->
        when (val result = gateway.guardianDefinition(url, token, id)) {
            is ApiResult.Success -> result.value.let {
                if (!it.success) ApiResult.Problem(if (it.problemCode == "guardian.workload_not_found") 404 else 503, it.problemCode, null)
                else if (it.definition?.id == id) ApiResult.Success(it.definition) else ApiResult.Transport("Guardian definition target mismatch.")
            }
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }
    }

    suspend fun save(owner: SessionState.Active, expected: GuardianDefinition?, requested: GuardianDefinition,
        approval: GuardianApproval?): ApiResult<GuardianDefinition> {
        try { return mutations.withLock {
            require(expected == null || expected.id == requested.id)
            // Preflight is repeated inside authenticated(), so a refresh cannot silently replace
            // a definition that changed while administrator credentials were being requested.
            when (val result = owned(owner) { url, token ->
                when (val before = gateway.guardianDefinition(url, token, requested.id).also { guardOwner(owner) }) {
                    is ApiResult.Success -> {
                        val actual = before.value
                        val matched = if (expected == null) !actual.success && actual.problemCode == "guardian.workload_not_found"
                            else actual.success && actual.definition == expected
                        if (!matched) {
                            if (!actual.success && actual.problemCode != "guardian.workload_not_found") ApiResult.Problem(503, actual.problemCode, null)
                            else ApiResult.Problem(409, "guardian.definition_changed", null)
                        } else gateway.guardianSave(url, token, requested, approval)
                    }
                    is ApiResult.Problem -> before
                    is ApiResult.Transport -> before
                }
            }) {
                is ApiResult.Success -> {
                    if (!result.value.success) return@withLock failed(GuardianOperation(false, result.value.problemCode))
                    val receipt = result.value.definition
                    if (receipt == null || !matchesReceipt(requested, receipt)) return@withLock ApiResult.Transport("Guardian save receipt could not be verified.")
                    when (val actual = definition(owner, requested.id)) {
                        is ApiResult.Success -> if (actual.value == receipt) actual
                            else ApiResult.Transport("Guardian saved definition could not be verified.")
                        else -> ApiResult.Transport("Guardian save read-back unavailable.")
                    }
                }
                is ApiResult.Problem -> result
                is ApiResult.Transport -> result
            }
        } } finally { approval?.password?.fill('\u0000') }
    }
    suspend fun action(owner: SessionState.Active, id: String, action: String): ApiResult<GuardianOperation> = mutations.withLock {
        require(action in listOf("start", "stop", "restart"))
        normalize(owned(owner) { url, token -> gateway.guardianAction(url, token, id, action) })
    }
    suspend fun delete(owner: SessionState.Active, id: String): ApiResult<GuardianOperation> = mutations.withLock {
        normalize(owned(owner) { url, token -> gateway.guardianDelete(url, token, id) })
    }
    private fun normalize(result: ApiResult<GuardianOperation>): ApiResult<GuardianOperation> =
        if (result is ApiResult.Success && !result.value.success) failed(result.value) else result
    private fun failed(result: GuardianOperation): ApiResult.Problem = ApiResult.Problem(
        if (result.problemCode in listOf("guardian.agent_timeout", "guardian.agent_unavailable", "guardian.agent_invalid_response")) 503 else 400,
        result.problemCode, null)
    private fun guardOwner(owner: SessionState.Active) { if (session.state.value !== owner) throw CancellationException("Guardian owner changed.") }
    private suspend fun <T> owned(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> = pipe.withLock {
        fun guard() { if (session.state.value !== owner) throw CancellationException("Guardian owner changed.") }
        guard()
        val result = try {
            session.authenticated { url, token -> guard(); call(url, token).also { guard() } }
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (_: Exception) {
            ApiResult.Transport(null)
        }
        guard()
        result
    }
    companion object {
        internal fun matchesReceipt(requested: GuardianDefinition, receipt: GuardianDefinition): Boolean {
            // This is the response to this save, bound by Server authorization, rather than an
            // arbitrary later GET. The only normalized fields are the host path and OS identity.
            val absolute = receipt.executablePath.startsWith("/") || Regex("^[A-Za-z]:[\\\\/].+").matches(receipt.executablePath) || receipt.executablePath.startsWith("\\\\")
            return absolute && !receipt.runAs.isNullOrBlank() && !receipt.runAsIdentity.isNullOrBlank() &&
                requested.copy(runAs = receipt.runAs, runAsIdentity = receipt.runAsIdentity, executablePath = receipt.executablePath) == receipt
        }
    }
}
