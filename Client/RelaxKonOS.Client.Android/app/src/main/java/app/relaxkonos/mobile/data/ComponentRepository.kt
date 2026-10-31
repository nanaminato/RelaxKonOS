package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ComponentRelease
import app.relaxkonos.mobile.core.net.ComponentReleases
import app.relaxkonos.mobile.core.net.ComponentState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ComponentChange
import app.relaxkonos.mobile.core.net.ComponentFrozenInstallationRequest
import app.relaxkonos.mobile.core.net.ComponentPinnedArchive
import app.relaxkonos.mobile.core.net.ComponentPlanResult
import app.relaxkonos.mobile.core.net.ComponentStateView
import app.relaxkonos.mobile.core.net.ComponentWire
import app.relaxkonos.mobile.core.net.InstallationKind
import app.relaxkonos.mobile.core.net.InstallationOperation
import app.relaxkonos.mobile.core.net.InstallationRequest
import app.relaxkonos.mobile.core.net.InstallationService
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.core.net.WebServer
import java.util.UUID
import kotlinx.coroutines.async
import kotlinx.coroutines.CancellationException

/**
 * One component as this server reports it. A row always has the stable component id; [view] is null
 * when this specific component could not be read, and [problem] then names the stable reason.
 */
data class ComponentRead(val componentId: String, val view: ComponentStateView?, val problem: String?)

/**
 * Reads component state and freezes changes for the hosting domain's installation ledger. It never
 * executes a plan itself: applying submits the frozen target through [InstallationRepository], so
 * approval, elevation, idempotency and recovery stay identical to installing from that application.
 *
 * Mihomo and sing-box keep their runtime pages; their changes are frozen by the runtime plan endpoint,
 * not by the component lifecycle routes.
 */
class ComponentRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
    private val officialReleases: suspend (String, String) -> List<ComponentRelease> =
        ComponentReleases::official,
    private val submitChange: suspend (SessionState.Active, InstallationKind, InstallationRequest,
        ElevationAnswerProvider) -> ApiResult<InstallationOperation>,
) {
    /** Both networks are tried independently, using the connected host platform. */
    suspend fun releases(owner: SessionState.Active, componentId: String, state: ComponentState): ApiResult<List<ComponentRelease>> = kotlinx.coroutines.supervisorScope {
        verify(owner)
        require(owner.privilegedOperations && state.componentId == componentId && state.mode == "managedArchive")
        val rid = ComponentReleases.rid(state)
        val requests = mutableListOf(
            async { try { kotlinx.coroutines.withTimeoutOrNull(35_000) { call(owner) { url, token -> gateway.componentReleases(url, token, componentId, rid) } } ?: ApiResult.Transport(null) }
                catch (cancelled: CancellationException) { throw cancelled }
                catch (_: Exception) { ApiResult.Transport(null) } },
            async { try { kotlinx.coroutines.withTimeoutOrNull(35_000) { ApiResult.Success(officialReleases(componentId, rid)) } ?: ApiResult.Transport(null) }
                catch (cancelled: CancellationException) { throw cancelled }
                catch (_: Exception) { ApiResult.Transport(null) } }
        )
        var emptySuccess = false
        try {
            while (requests.isNotEmpty()) {
                val (request, result) = kotlinx.coroutines.selects.select {
                    requests.forEach { request -> request.onAwait { request to it } }
                }
                requests.remove(request)
                verify(owner)
                if (result is ApiResult.Success) {
                    if (result.value.isNotEmpty()) return@supervisorScope result
                    emptySuccess = true
                }
            }
            if (emptySuccess) ApiResult.Success(emptyList()) else ApiResult.Problem(503, "component.package_index_unavailable", null)
        } finally { requests.forEach { it.cancel() } }
    }

    suspend fun read(owner: SessionState.Active, componentIds: List<String>): ApiResult<List<ComponentRead>> {
        verify(owner)
        require(owner.privilegedOperations) { "Component state needs the privileged-operations host surface" }
        val ids = componentIds.distinct()
        require(ids.isNotEmpty()) { "No components to read" }

        val rows = ArrayList<ComponentRead>(ids.size)
        var transport: ApiResult.Transport? = null
        var readable = false
        for (id in ids) {
            val result = try {
                session.authenticated { url, token ->
                    verify(owner)
                    gateway.componentState(url, token, id)
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                verify(owner)
                ApiResult.Transport(null)
            }
            verify(owner)
            when (result) {
                is ApiResult.Success -> {
                    readable = true
                    rows += ComponentRead(id, result.value, null)
                }
                is ApiResult.Problem -> {
                    // Missing authorization is a screen-level refusal, not a per-row state.
                    if (result.status in setOf(401, 403)) return result
                    rows += ComponentRead(id, null, result.code ?: "component.unavailable")
                }
                is ApiResult.Transport -> {
                    transport = result
                    rows += ComponentRead(id, null, null)
                }
            }
        }
        if (!readable && transport != null) return transport
        return ApiResult.Success(rows)
    }

    /** Freezes install/update/uninstall against the exact state the user was shown. */
    suspend fun plan(owner: SessionState.Active, componentId: String, change: ComponentChange,
        state: ComponentState, keepData: Boolean, pin: ComponentPinnedArchive? = null,
        pinRevision: Long = 0): ApiResult<ComponentPlanResult> {
        verify(owner)
        require(owner.privilegedOperations)
        require(change != ComponentChange.Uninstall || state.ownership != "absent") { "Nothing to uninstall" }
        // Only a first install of an archive component names a pinned release; a package change never
        // carries one, and an update or removal must not pretend to.
        require(pin == null || change == ComponentChange.Install) { "A pinned archive is an install target only" }
        val body = ComponentWire.planBody(componentId, change, state, keepData, UUID.randomUUID().toString(), pin, pinRevision)
        return call(owner) { url, token -> gateway.componentPlan(url, token, componentId, change, body) }
    }

    /**
     * Submits a frozen plan through the domain installation ledger. The service mapping is explicit:
     * an unknown component has no installation owner and is refused instead of guessing one.
     */
    suspend fun apply(owner: SessionState.Active, plan: ComponentPlanResult,
        provider: ElevationAnswerProvider): ApiResult<InstallationOperation> {
        verify(owner)
        require(plan.allowed) { "The host did not allow this change" }
        val service = serviceFor(plan.componentId) ?: return ApiResult.Transport("No installation owner for ${plan.componentId}")
        val kind = when (plan.change) {
            ComponentChange.Install -> InstallationKind.Install
            ComponentChange.Update -> InstallationKind.Upgrade
            ComponentChange.Uninstall -> InstallationKind.Uninstall
        }
        val request = ComponentFrozenInstallationRequest(
            confirmed = true,
            service = service,
            componentId = plan.componentId,
            planRequestJson = requireNotNull(plan.requestJson),
            expectedTargetJson = requireNotNull(plan.targetJson),
            archive = plan.componentId == "frpc" || plan.componentId == "frps",
            allowedKinds = setOf(kind),
        )
        return try {
            submitChange(owner, kind, request, provider)
        } catch (_: IllegalArgumentException) {
            // No installation owner for this service (module disabled, capability absent, kind
            // unsupported): report a stable refusal instead of treating it as a transport failure.
            ApiResult.Problem(409, "installation.not_supported", null)
        }
    }

    suspend fun currentNginx(owner: SessionState.Active): ApiResult<String?> {
        verify(owner)
        require(owner.privilegedOperations)
        return call(owner) { url, token -> gateway.currentNginx(url, token) }
    }

    /** Managed or integrated Nginx instances only; the server already filters ownership. */
    suspend fun nginxServers(owner: SessionState.Active): ApiResult<List<WebServer>> {
        verify(owner)
        require(owner.privilegedOperations)
        return call(owner) { url, token -> gateway.webServers(url, token) }
    }

    suspend fun selectCurrentNginx(owner: SessionState.Active, serverId: String): ApiResult<String?> {
        verify(owner)
        require(owner.privilegedOperations)
        require(serverId.isNotBlank())
        return call(owner) { url, token ->
            gateway.selectCurrentNginx(url, token, serverId)
        }
    }

    private suspend fun <T> call(owner: SessionState.Active,
        request: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner)
        return session.authenticated { url, token -> verify(owner); request(url, token) }.also { verify(owner) }
    }

    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Component session changed")
    }

    private fun serviceFor(componentId: String): InstallationService? = when (componentId) {
        "nginx" -> InstallationService.Nginx
        "docker" -> InstallationService.Docker
        "samba" -> InstallationService.Smb
        "squid" -> InstallationService.Squid
        "tailscale" -> InstallationService.Tailscale
        "frpc", "frps" -> InstallationService.Frp
        else -> null
    }
}
