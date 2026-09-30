package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

class SmbRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val elevations: ElevationRepository, private val journal: SmbMutationJournal) {
    private val mutations = Mutex()
    fun pending(owner: SessionState.Active) = journal.pending(owner.also(::verify))
    suspend fun facts(owner: SessionState.Active): ApiResult<SmbFacts> {
        val caps = call(owner, gateway::smbCapabilities); if (caps !is ApiResult.Success) return failure(caps)
        val status = call(owner, gateway::smbStatus); if (status !is ApiResult.Success) return failure(status)
        val connection = call(owner, gateway::smbConnection); if (connection !is ApiResult.Success) return failure(connection)
        val manageable = caps.value.supported && status.value.state.manageable
        val shares = if (manageable && caps.value.managedSharesSupported) call(owner, gateway::smbShares) else null
        if (shares != null && shares !is ApiResult.Success) return failure(shares)
        val users = if (manageable && caps.value.sambaCredentialsSupported) call(owner, gateway::smbUsers) else null
        if (users != null && users !is ApiResult.Success) return failure(users)
        verify(owner)
        return ApiResult.Success(SmbFacts(caps.value, status.value, shares?.value, users?.value, connection.value))
    }
    suspend fun change(owner: SessionState.Active, expected: SmbFacts, change: SmbChange, password: CharArray?, provider: ElevationAnswerProvider): ApiResult<SmbReceipt> = try {
        mutations.withLock {
            verify(owner); require(owner.privilegedOperations && expected.capabilities.supported && expected.status.state.manageable)
            require(journal.pending(owner).isEmpty()); validate(expected, change, password)
            val before = facts(owner); if (before !is ApiResult.Success) return@withLock failure(before)
            if (before.value != expected) return@withLock ApiResult.Problem(409, "file-services.smb.facts_changed", null)
            val pending = journal.begin(owner, change)
            val result = elevations.withElevation("smbManage", "smb:managed", provider) { url, token ->
                verify(owner)
                val latest = facts(owner); if (latest !is ApiResult.Success) return@withElevation failure(latest)
                if (latest.value != expected) return@withElevation ApiResult.Problem(409, "file-services.smb.facts_changed", null)
                gateway.smbChange(url, token, change, password).let {
                    if (it is ApiResult.Problem && it.code == "file-services.smb.elevation_required")
                        ApiResult.Problem(it.status, ProblemCodes.ELEVATION_REQUIRED, it.traceId) else it
                }
            }
            verify(owner)
            if (result is ApiResult.Problem && result.code in setOf(ProblemCodes.ELEVATION_REQUIRED, ProblemCodes.UNAUTHORIZED)) journal.complete(pending)
            if (result is ApiResult.Success) {
                journal.receipt(pending, result.value.operationId)
                if (result.value.succeeded) {
                    val current = facts(owner)
                    if (current is ApiResult.Success && current.value.capabilities.supported && current.value.status.state.manageable) journal.complete(pending)
                }
            }
            result
        }
    } finally { password?.fill('\u0000') }
    suspend fun acceptFacts(owner: SessionState.Active, pending: PendingSmbMutation): ApiResult<SmbFacts> = mutations.withLock {
        verify(owner); require(pending in journal.pending(owner))
        facts(owner).also { if (it is ApiResult.Success && it.value.capabilities.supported && it.value.status.state.manageable) journal.complete(pending) }
    }
    private fun validate(facts: SmbFacts, change: SmbChange, password: CharArray?) {
        when (change.kind) {
            SmbChangeKind.Start -> require(facts.status.state == SmbRuntimeState.Stopped)
            SmbChangeKind.Stop, SmbChangeKind.Restart -> require(facts.status.state == SmbRuntimeState.Running)
            SmbChangeKind.CreateShare, SmbChangeKind.UpdateShare, SmbChangeKind.DeleteShare -> {
                require(facts.capabilities.managedSharesSupported && facts.shares != null)
                if (change.kind != SmbChangeKind.CreateShare) require(facts.shares.any { it.id == change.target && it.managed && !it.drifted })
                if (change.kind != SmbChangeKind.DeleteShare) require(SmbValidation.share(requireNotNull(change.share), facts.capabilities.windowsShareSecuritySupported))
            }
            SmbChangeKind.EnableUser, SmbChangeKind.DisableUser, SmbChangeKind.Password -> {
                require(facts.capabilities.sambaCredentialsSupported && facts.users?.any { it.username == change.target && it.eligible } == true)
                if (change.kind == SmbChangeKind.Password) require(SmbValidation.password(requireNotNull(password)))
            }
        }
    }
    private suspend fun <T> call(owner: SessionState.Active, request: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner); return session.authenticated { url, token -> verify(owner); request(url, token) }.also { verify(owner) }
    }
    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("SMB session changed")
        require(ServerCapabilities.FILE_SERVICES in owner.capabilities)
    }
    private fun <T> failure(result: ApiResult<*>): ApiResult<T> = when (result) { is ApiResult.Problem -> result; is ApiResult.Transport -> result; is ApiResult.Success -> error("Expected failed read") }
}
