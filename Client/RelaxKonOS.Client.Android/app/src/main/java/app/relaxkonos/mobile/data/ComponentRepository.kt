package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ComponentStateView
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import kotlinx.coroutines.CancellationException

/**
 * One component as this server reports it. A row always has the stable component id; [view] is null
 * when this specific component could not be read, and [problem] then names the stable reason
 * (`component.unknown`, `component.platform_unsupported`, or the transport marker). One unreadable
 * component never hides the others.
 */
data class ComponentRead(val componentId: String, val view: ComponentStateView?, val problem: String?)

/**
 * Reads normalized component state for the Android component center. It performs no lifecycle work:
 * there is no plan, execute, install, update or uninstall call in this class, because those belong to
 * the host component center where the frozen plan, approval and recovery ledger live.
 */
class ComponentRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
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

    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Component session changed")
    }
}
