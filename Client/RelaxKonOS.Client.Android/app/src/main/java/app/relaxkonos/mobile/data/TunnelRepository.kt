package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

data class TunnelFacts(val profiles: List<TunnelProfile>, val definitions: List<TunnelDefinition>, val observedAtMillis: Long)
class TunnelRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val elevations: ElevationRepository, private val journal: TunnelMutationJournal) {
    private val mutations = Mutex()
    fun pending(owner: SessionState.Active): List<PendingTunnelMutation> { verify(owner); return journal.pending(owner) }
    suspend fun frps(owner: SessionState.Active) = read(owner) { u, t -> gateway.managedFrps(u, t) }
    suspend fun frpsEditing(owner: SessionState.Active, revision: Long): ApiResult<ManagedFrpsEditing> {
        verify(owner); require(owner.privilegedOperations)
        var received: ManagedFrpsEditing? = null
        try {
            val result = read(owner) { u, t -> gateway.managedFrpsEditing(u, t).also { if (it is ApiResult.Success) received = it.value } }
            if (result is ApiResult.Success && result.value.configuration.revision != revision) {
                result.value.token?.fill('\u0000'); return ApiResult.Problem(409, "tunnel.revision_conflict", null)
            }
            return result
        } catch (error: Throwable) { received?.token?.fill('\u0000'); throw error }
    }
    suspend fun frpsLogs(owner: SessionState.Active) = read(owner) { u, t -> gateway.managedFrpsLogs(u, t) }
    suspend fun frpsAudit(owner: SessionState.Active) = read(owner) { u, t -> gateway.managedFrpsAudit(u, t) }
    suspend fun saveFrps(owner: SessionState.Active, request: ManagedFrpsRequest): ApiResult<ManagedFrps> = try {
        mutate(owner, TunnelMutation.SaveFrps, null, null) {
            read(owner) { u, t -> gateway.saveManagedFrps(u, t, request) }.let {
                if (it is ApiResult.Success && it.value.revision <= request.expectedRevision) ApiResult.Transport(null) else it
            }
        }
    } finally { request.token?.fill('\u0000'); request.dashboardPassword?.fill('\u0000') }
    suspend fun frpsLifecycle(owner: SessionState.Active, action: TunnelMutation, provider: ElevationAnswerProvider): ApiResult<TunnelResult> {
        require(action in setOf(TunnelMutation.StartFrps, TunnelMutation.StopFrps, TunnelMutation.RestartFrps))
        return mutate(owner, action, null, null) {
            suspend fun call(start: Boolean): ApiResult<TunnelResult> = elevations.withElevation("frpLifecycle", "frps", provider) { u, t ->
                verify(owner)
                if (start) gateway.startManagedFrps(u, t) else gateway.stopManagedFrps(u, t)
            }
            if (action == TunnelMutation.RestartFrps) when (val stopped = call(false)) {
                is ApiResult.Success -> {
                    if (!stopped.value.succeeded) return@mutate stopped
                    if (stopped.value.state != TunnelConnectionState.Disconnected) return@mutate ApiResult.Transport(null)
                }
                is ApiResult.Problem -> return@mutate stopped
                is ApiResult.Transport -> return@mutate stopped
            }
            // Each leg owns its authentication/elevation retry so a start challenge cannot repeat a completed stop.
            val result = call(action != TunnelMutation.StopFrps)
            if (result is ApiResult.Success && result.value.succeeded &&
                (if (action == TunnelMutation.StopFrps) result.value.state != TunnelConnectionState.Disconnected
                 else result.value.state !in setOf(TunnelConnectionState.Connected, TunnelConnectionState.Starting))) ApiResult.Transport(null) else result
        }
    }
    suspend fun acceptFrpsFacts(owner: SessionState.Active, pending: PendingTunnelMutation): ApiResult<ManagedFrps> = mutations.withLock {
        verify(owner); require(pending.action.frps && pending in journal.pending(owner))
        val result = frps(owner); if (result is ApiResult.Success) journal.complete(pending); result
    }
    suspend fun profiles(owner: SessionState.Active) = read(owner) { u, t -> gateway.tunnelProfiles(u, t) }
    suspend fun definitions(owner: SessionState.Active) = read(owner) { u, t -> gateway.tunnelDefinitions(u, t) }
    suspend fun runtime(owner: SessionState.Active) = read(owner) { u, t -> gateway.tunnelRuntime(u, t) }
    suspend fun download(owner: SessionState.Active, version: String): ApiResult<TunnelRuntimeDownload> = read(owner) { u, t -> gateway.tunnelRuntimeDownload(u, t, version) }.let {
        if (it is ApiResult.Success && it.value.version != version) ApiResult.Transport(null) else it
    }
    suspend fun detect(owner: SessionState.Active, path: String): ApiResult<TunnelRuntime> { verify(owner); require(owner.privilegedOperations); return read(owner) { u, t -> gateway.detectTunnelRuntime(u, t, path) } }
    suspend fun logs(owner: SessionState.Active, id: String) = read(owner) { u, t -> gateway.tunnelLogs(u, t, InstallationRoutes.canonicalId(id)) }
    suspend fun facts(owner: SessionState.Active): ApiResult<TunnelFacts> {
        val profiles = profiles(owner); if (profiles !is ApiResult.Success) return when (profiles) { is ApiResult.Problem -> profiles; is ApiResult.Transport -> profiles; else -> error("Unreachable") }
        val definitions = definitions(owner); if (definitions !is ApiResult.Success) return when (definitions) { is ApiResult.Problem -> definitions; is ApiResult.Transport -> definitions; else -> error("Unreachable") }
        if (definitions.value.any { item -> profiles.value.none { it.id == item.profileId } }) return ApiResult.Transport(null)
        return ApiResult.Success(TunnelFacts(profiles.value, definitions.value, System.currentTimeMillis()))
    }
    suspend fun acceptFacts(owner: SessionState.Active, pending: PendingTunnelMutation): ApiResult<TunnelFacts> = mutations.withLock {
        verify(owner); require(!pending.action.frps && pending in journal.pending(owner))
        val result = facts(owner)
        if (result is ApiResult.Success) journal.complete(pending)
        result
    }
    suspend fun saveProfile(owner: SessionState.Active, id: String?, request: TunnelProfileRequest): ApiResult<TunnelProfile> =
        mutate(owner, TunnelMutation.SaveProfile, id, id) {
            read(owner) { u, t -> gateway.saveTunnelProfile(u, t, id, request) }.let {
                if (it is ApiResult.Success && ((id != null && it.value.id != id) || (request.expectedRevision != null && it.value.revision <= request.expectedRevision))) ApiResult.Transport(null) else it
            }
        }
    suspend fun deleteProfile(owner: SessionState.Active, profile: TunnelProfile, provider: ElevationAnswerProvider): ApiResult<Unit> = mutate(owner, TunnelMutation.DeleteProfile, profile.id, profile.id) {
        val id = profile.id
        when (val current = facts(owner)) {
            is ApiResult.Success -> {
                if (current.value.profiles.firstOrNull { it.id == id }?.revision != profile.revision) return@mutate ApiResult.Problem(409, "tunnel.revision_conflict", null)
                if (current.value.definitions.any { it.profileId == id }) return@mutate ApiResult.Problem(409, "tunnel.profile_in_use", null)
            }
            is ApiResult.Problem -> return@mutate current
            is ApiResult.Transport -> return@mutate current
        }
        // Stop first while the owned profile still exists. A lost stop response blocks deletion and requires factual observation.
        when (val stopped = elevations.withElevation("frpLifecycle", id, provider) { u, t -> verify(owner); gateway.stopTunnelProfile(u, t, id) }) {
            is ApiResult.Success -> if (stopped.value.succeeded) read(owner) { u, t -> gateway.deleteTunnelProfile(u, t, id) } else ApiResult.Problem(409, "tunnel.profile_stop_failed", null)
            is ApiResult.Problem -> stopped
            is ApiResult.Transport -> stopped
        }
    }
    suspend fun setToken(owner: SessionState.Active, id: String, token: String) = mutate(owner, TunnelMutation.SetToken, id, id) { read(owner) { u, t -> gateway.setTunnelToken(u, t, id, token) } }
    suspend fun saveDefinition(owner: SessionState.Active, id: String?, request: TunnelDefinitionRequest): ApiResult<TunnelDefinition> =
        mutate(owner, TunnelMutation.SaveDefinition, id, request.profileId) {
            read(owner) { u, t -> gateway.saveTunnelDefinition(u, t, id, request) }.let {
                if (it is ApiResult.Success && (it.value.profileId != request.profileId || (id != null && it.value.id != id) || (request.expectedRevision != null && it.value.revision <= request.expectedRevision))) ApiResult.Transport(null) else it
            }
        }
    suspend fun deleteDefinition(owner: SessionState.Active, definition: TunnelDefinition) = mutate(owner, TunnelMutation.DeleteDefinition, definition.id, definition.profileId) { read(owner) { u, t -> gateway.deleteTunnelDefinition(u, t, definition.id) } }
    suspend fun lifecycle(owner: SessionState.Active, id: String, apply: Boolean, provider: ElevationAnswerProvider): ApiResult<TunnelResult> =
        mutate(owner, if (apply) TunnelMutation.Apply else TunnelMutation.Stop, id, id) {
            elevations.withElevation("frpLifecycle", id, provider) { u, t -> verify(owner); if (apply) gateway.applyTunnelProfile(u, t, id) else gateway.stopTunnelProfile(u, t, id) }
        }
    private suspend fun <T> mutate(owner: SessionState.Active, action: TunnelMutation, id: String?, profileId: String?, call: suspend () -> ApiResult<T>): ApiResult<T> = mutations.withLock {
        verify(owner); require(owner.privilegedOperations)
        val pending = PendingTunnelMutation(owner.serviceId, owner.userName, action, id?.let(InstallationRoutes::canonicalId), profileId?.let(InstallationRoutes::canonicalId))
        if (!journal.begin(pending)) return@withLock ApiResult.Problem(409, "tunnel.observation_pending", null)
        val result = call(); verify(owner)
        if (result is ApiResult.Success || result is ApiResult.Problem && result.status in setOf(400, 401, 403, 404, 409)) journal.complete(pending)
        result
    }
    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner)
        return session.authenticated { u, t -> verify(owner); call(u, t) }.also { verify(owner) }
    }
    private fun verify(owner: SessionState.Active) { if (session.state.value !== owner) throw CancellationException("Tunnel session changed"); require(ServerCapabilities.TUNNELS in owner.capabilities) }
}
