package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.UUID

class HostSettingsRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val elevations: ElevationRepository, private val journal: HostSettingsJournal) {
    private val writes = Mutex()
    fun references(owner: SessionState.Active) = journal.references(owner.also(::verify))
    suspend fun time(owner: SessionState.Active) = read(owner, gateway::hostTime)
    suspend fun identity(owner: SessionState.Active) = read(owner, gateway::hostIdentity)
    suspend fun environment(owner: SessionState.Active, scope: HostEnvironmentScope, reveal: Boolean, provider: ElevationAnswerProvider): ApiResult<HostEnvironmentSettings> {
        val target = read(owner) { u,t -> gateway.hostEnvironmentTarget(u,t,scope) }
        if (target !is ApiResult.Success) return fail(target)
        val result = elevated(owner, if (reveal) "hostEnvironmentReveal" else "hostEnvironmentRead", target.value.resourceId, provider) { u,t -> gateway.hostEnvironment(u,t,scope,reveal) }
        return if (result is ApiResult.Success && result.value.target != target.value) ApiResult.Transport(null) else result
    }
    suspend fun preview(owner: SessionState.Active, kind: HostSettingKind, revision: String, target: HostSettingsTarget,
        value: String?, scope: HostEnvironmentScope?, mutation: HostEnvironmentMutation?, confirmHighImpact: Boolean,
        provider: ElevationAnswerProvider): ApiResult<HostSettingsPlan> = writes.withLock {
        manage(owner); require(references(owner).none { it.unresolved })
        require(HostSettingsRules.revision(revision)); validateTarget(kind, target)
        if (kind == HostSettingKind.Environment) {
            require(scope?.query == target.scope && mutation != null && HostSettingsRules.mutation(mutation, owner.serverPlatform.equals("windows", true)))
            require(!HostSettingsRules.highImpact(mutation.name, owner.serverPlatform.equals("windows", true)) || confirmHighImpact)
        }
        val key = UUID.randomUUID().toString()
        val call: suspend (String,String) -> ApiResult<HostSettingsPlan> = { u,t -> verify(owner); gateway.previewHostSettings(u,t,kind,revision,key,value,scope,mutation,confirmHighImpact) }
        val result = if (kind == HostSettingKind.Environment) elevated(owner,"hostEnvironmentRead",target.resourceId,provider,call) else read(owner,call)
        if (result is ApiResult.Success) {
            val plan = result.value
            if (plan.target != target || plan.expectedRevision != revision || plan.requiredCapability != kind.capability || plan.authorizationTarget != target.resourceId ||
                IsoInstant.requireEpochMillis(plan.expiresAt) <= System.currentTimeMillis()) return@withLock ApiResult.Transport(null)
            // Preview has no host effect. The original plan is persisted before the first apply.
        }
        result
    }
    suspend fun apply(owner: SessionState.Active, kind: HostSettingKind, plan: HostSettingsPlan, provider: ElevationAnswerProvider): ApiResult<HostSettingsOperation> = writes.withLock {
        manage(owner); validateTarget(kind,plan.target); require(plan.requiredCapability == kind.capability && plan.authorizationTarget == plan.target.resourceId)
        require(IsoInstant.requireEpochMillis(plan.expiresAt) > System.currentTimeMillis())
        val reference = journal.record(owner,kind,plan)
        val result = elevated(owner,kind.capability,plan.target.resourceId,provider) { u,t ->
            verify(owner); gateway.applyHostSettings(u,t,kind,plan.id)
        }
        settle(owner,reference,result)
    }
    suspend fun operation(owner: SessionState.Active, reference: HostSettingsReference): ApiResult<HostSettingsOperation> {
        verifyReference(owner,reference)
        return settle(owner,reference,read(owner) { u,t -> gateway.hostSettingsOperation(u,t,reference.id) })
    }
    suspend fun rollback(owner: SessionState.Active, reference: HostSettingsReference, expected: HostSettingsOperation,
        provider: ElevationAnswerProvider): ApiResult<HostSettingsOperation> = writes.withLock {
        manage(owner); verifyReference(owner,reference)
        require(expected.id == reference.id && expected.target == reference.target && expected.state == "applied" && expected.observedRevision != null)
        val current = operation(owner,reference)
        if (current !is ApiResult.Success) return@withLock fail(current)
        if (current.value != expected) return@withLock ApiResult.Problem(409,"settings.revision_conflict",null)
        val pending = journal.beginRollback(reference)
        val result = elevated(owner,reference.kind.capability,reference.target.resourceId,provider) { u,t ->
            verify(owner); gateway.rollbackHostSettings(u,t,reference.id,expected.observedRevision)
        }
        settle(owner,pending,result)
    }
    private fun settle(owner: SessionState.Active, ref: HostSettingsReference, result: ApiResult<HostSettingsOperation>): ApiResult<HostSettingsOperation> {
        verify(owner)
        if (result is ApiResult.Success) {
            if (result.value.state in setOf("applied","rolledBack") && result.value.observedRevision == null || result.value.id != ref.id || result.value.target != ref.target || result.value.settingId != when (ref.kind) {
                    HostSettingKind.Time -> "host.time.zone"; HostSettingKind.Identity -> "host.identity.hostname"; HostSettingKind.Environment -> "host.environment" }) return ApiResult.Transport(null)
            if (result.value.terminal) journal.resolved(ref)
        }
        return result
    }
    private fun validateTarget(kind: HostSettingKind, target: HostSettingsTarget) {
        if (kind != HostSettingKind.Environment) require(target == HostSettingsTarget(kind.resource,"hostMachine",null))
        else require(target == HostSettingsTarget("host/environment/machine","hostMachine",null) || target.scope == "hostUser" &&
            !target.platformIdentity.isNullOrBlank() && target.resourceId == "host/environment/user/${target.platformIdentity}")
    }
    private fun verifyReference(owner: SessionState.Active, ref: HostSettingsReference) { verify(owner); require(ref.serviceId == owner.serviceId && ref.account == owner.userName && references(owner).any { it.id == ref.id }); validateTarget(ref.kind, ref.target) }
    private fun manage(owner: SessionState.Active) { verify(owner); require(owner.privilegedOperations) }
    private fun verify(owner: SessionState.Active) { if (session.state.value !== owner) throw CancellationException("Settings session changed") }
    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String,String)->ApiResult<T>): ApiResult<T> {
        verify(owner); return session.authenticated { u,t -> verify(owner); call(u,t) }.also { verify(owner) }
    }
    private suspend fun <T> elevated(owner: SessionState.Active, capability: String, target: String, provider: ElevationAnswerProvider,
        call: suspend (String,String)->ApiResult<T>): ApiResult<T> {
        verify(owner)
        return elevations.withElevation(capability,target,provider) { u,t ->
            verify(owner)
            val result = call(u,t)
            if (result is ApiResult.Problem && result.code in setOf("settings.elevation_required","settings.environment.authorization_required"))
                ApiResult.Problem(result.status,ProblemCodes.ELEVATION_REQUIRED,null) else result
        }.also { verify(owner) }
    }
    private fun <T> fail(result: ApiResult<*>): ApiResult<T> = when(result) { is ApiResult.Problem -> result; is ApiResult.Transport -> result; is ApiResult.Success -> error("Expected failure") }
}
