package app.relaxkonos.mobile.ui.manage.components

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ComponentChange
import app.relaxkonos.mobile.core.net.ComponentPinnedArchive
import app.relaxkonos.mobile.core.net.ComponentPlanResult
import app.relaxkonos.mobile.core.net.WebServer
import app.relaxkonos.mobile.data.ComponentRead
import app.relaxkonos.mobile.data.ComponentRepository
import app.relaxkonos.mobile.data.ElevationAnswerProvider
import app.relaxkonos.mobile.data.InstallationRepository
import app.relaxkonos.mobile.core.net.FrpInstallationRequest
import app.relaxkonos.mobile.core.net.InstallationKind
import app.relaxkonos.mobile.core.net.InstallationRequest
import app.relaxkonos.mobile.core.net.InstallationService
import app.relaxkonos.mobile.core.net.NginxInstallationRequest
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class ComponentChangeReview(
    val componentId: String,
    val change: ComponentChange,
    val keepData: Boolean,
    val plan: ComponentPlanResult,
    val targetVersion: String? = null,
)

data class ComponentOperationNotice(val operationId: String)

data class NginxChoiceState(val current: String?, val servers: List<WebServer>)

/**
 * A file install needs the operator to name the version it contains and a live reference to an
 * archive on this host. Nginx and the FRP bundle support this path; everything else is refused here
 * rather than attempted and failing later.
 */
internal fun validFileInstall(componentId: String, version: String, referenceId: String): Boolean =
    componentId in ComponentCatalog.fileInstallableIds &&
        version.trim().matches(Regex("[A-Za-z0-9][A-Za-z0-9._-]{0,31}")) &&
        referenceId.isNotBlank()

/** The owning domain service that executes a file-based install for this component. */
internal fun fileInstallService(componentId: String): InstallationService? = when (componentId) {
    "nginx" -> InstallationService.Nginx
    "frpc", "frps" -> InstallationService.Frp
    else -> null
}

/** Nginx removal is a domain-ledger uninstall and names no other component. */
internal fun validNginxUninstall(componentId: String): Boolean = componentId == "nginx"

/**
 * FRP maintenance is an explicit choice: an upgrade names a target version, a rollback repairs the
 * previous one, and an uninstall removes the managed pair. A plain repair must name a version, so
 * "repair" without a rollback or a version is never submitted.
 */
internal fun validFrpMaintenance(componentId: String, kind: InstallationKind, version: String,
    rollback: Boolean): Boolean {
    if (componentId !in setOf("frpc", "frps")) return false
    val named = version.trim()
    val versionValid = named.matches(Regex("[A-Za-z0-9][A-Za-z0-9._-]{0,31}"))
    return when (kind) {
        InstallationKind.Upgrade -> versionValid
        InstallationKind.Repair -> rollback || versionValid
        InstallationKind.Uninstall -> !rollback
        else -> false
    }
}

data class ComponentCenterUiState(
    val loading: Boolean = false,
    val loaded: Boolean = false,
    val rows: List<ComponentRead> = emptyList(),
    val error: Boolean = false,
    val problemCode: String? = null,
    val busy: Boolean = false,
    val review: ComponentChangeReview? = null,
    val operation: ComponentOperationNotice? = null,
    val actionProblem: String? = null,
    val nginx: NginxChoiceState? = null,
    val nginxLoading: Boolean = false,
) {
    val recoveryVisible: Boolean get() = rows.any { it.view?.recoveryRequired == true }
}

/**
 * Loads component state and runs the three change actions the component center owns on Android:
 * install, update and uninstall. Plans are frozen by the host and applied through the domain
 * installation ledger; this class never invents a package, version or elevated command.
 *
 * A trusted archive may also be installed from a file that is already on the host. That path still
 * enters the same ledger, with the same elevation and idempotency, and only names a version the user
 * supplied and an operator-bound file reference.
 */
class ComponentCenterViewModel(
    private val session: AuthSession,
    private val repository: ComponentRepository,
    private val installations: InstallationRepository? = null,
    private val elevationAnswers: ElevationAnswerProvider? = null,
) : ViewModel() {
    private val mutable = MutableStateFlow(ComponentCenterUiState())
    val state = mutable.asStateFlow()

    private var owner: SessionState.Active? = null
    private var work: Job? = null
    private var action: Job? = null

    init {
        viewModelScope.launch {
            session.state.collect { value ->
                val active = value as? SessionState.Active
                if (owner !== active) {
                    work?.cancel()
                    action?.cancel()
                    owner = active
                    mutable.value = ComponentCenterUiState()
                }
            }
        }
    }

    fun load(active: SessionState.Active) {
        owner = active
        if (mutable.value.loading) return
        work?.cancel()
        work = viewModelScope.launch {
            mutable.value = mutable.value.copy(loading = true, error = false, problemCode = null)
            val result = try {
                repository.read(active, ComponentCatalog.ids)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                ApiResult.Transport(null)
            }
            if (session.state.value !== active) return@launch
            mutable.value = when (result) {
                is ApiResult.Success -> mutable.value.copy(rows = result.value, loading = false, loaded = true)
                is ApiResult.Problem -> ComponentCenterUiState(error = true, loaded = true, problemCode = result.code)
                is ApiResult.Transport -> ComponentCenterUiState(error = true, loaded = true)
            }
        }
    }

    fun refresh(active: SessionState.Active) {
        if (mutable.value.loading || session.state.value !== active) return
        load(active)
    }

    /** Freezes the change and opens the confirmation; a refused plan becomes a visible problem. */
    fun requestChange(active: SessionState.Active, componentId: String, change: ComponentChange,
        keepData: Boolean = true, pin: ComponentPinnedArchive? = null) {
        if (mutable.value.busy || session.state.value !== active) return
        val view = mutable.value.rows.firstOrNull { it.componentId == componentId }?.view ?: return
        val component = view.state ?: return
        // The host only grants changes to an absent (install) or managed (update/uninstall) component.
        if (change == ComponentChange.Install) { if (component.ownership != "absent") return }
        else if (component.ownership != "managed") return
        action?.cancel()
        action = viewModelScope.launch {
            mutable.value = mutable.value.copy(busy = true, actionProblem = null)
            val result = try {
                repository.plan(active, componentId, change, component, keepData, pin, view.pinRevision)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: IllegalArgumentException) {
                // A pinned archive is only an install target; the screen never sends it for another change.
                ApiResult.Problem(409, "component.request_invalid", null)
            } catch (_: Exception) {
                ApiResult.Transport(null)
            }
            if (session.state.value !== active) return@launch
            mutable.value = when (result) {
                is ApiResult.Success -> if (result.value.allowed) {
                    mutable.value.copy(busy = false,
                        review = ComponentChangeReview(componentId, change, keepData, result.value, pin?.version))
                } else {
                    mutable.value.copy(busy = false,
                        actionProblem = result.value.problems.firstOrNull() ?: "component.plan_refused")
                }
                is ApiResult.Problem -> mutable.value.copy(busy = false, actionProblem = result.code ?: "component.plan_refused")
                is ApiResult.Transport -> mutable.value.copy(busy = false, actionProblem = "component.unreachable")
            }
        }
    }

    /** Applies the reviewed plan through the installation ledger, including the elevation prompt. */
    fun confirmChange(active: SessionState.Active, provider: ElevationAnswerProvider) {
        val review = mutable.value.review ?: return
        if (mutable.value.busy || session.state.value !== active || review.componentId.isEmpty()) return
        action?.cancel()
        action = viewModelScope.launch {
            mutable.value = mutable.value.copy(busy = true, actionProblem = null)
            val result = try {
                repository.apply(active, review.plan, provider)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                ApiResult.Transport(null)
            }
            if (session.state.value !== active) return@launch
            mutable.value = when (result) {
                is ApiResult.Success -> mutable.value.copy(busy = false, review = null,
                    operation = ComponentOperationNotice(result.value.operationId))
                is ApiResult.Problem -> mutable.value.copy(busy = false, actionProblem = result.code ?: "installation.failed")
                is ApiResult.Transport -> mutable.value.copy(busy = false, actionProblem = "installation.unreachable")
            }
            if (result is ApiResult.Success) load(active)
        }
    }

    /**
     * Installs a pinned archive from a file already on the host. The version is what the operator
     * says the archive contains; the domain still verifies the archive, its platform and identity.
     */
    fun installArchiveFromFile(active: SessionState.Active, componentId: String, version: String, referenceId: String) {
        if (mutable.value.busy || session.state.value !== active) return
        if (!validFileInstall(componentId, version, referenceId)) return
        val target = version.trim()
        val request: InstallationRequest = when (componentId) {
            "nginx" -> NginxInstallationRequest(true, target, referenceId)
            "frpc", "frps" -> FrpInstallationRequest(true, target, fileReferenceId = referenceId)
            else -> return
        }
        submit(active, InstallationKind.Install, request)
    }

    /**
     * Changes an installed FRP runtime from the one entry the plan keeps. The version (or the explicit
     * rollback) is the operator's choice; the domain service still verifies releases and integrity.
     */
    fun maintainFrp(active: SessionState.Active, componentId: String, kind: InstallationKind,
        version: String? = null, rollback: Boolean = false) {
        if (mutable.value.busy || session.state.value !== active) return
        if (!validFrpMaintenance(componentId, kind, version.orEmpty(), rollback)) return
        val request = FrpInstallationRequest(true, version?.trim()?.takeIf(String::isNotBlank), rollback)
        submit(active, kind, request)
    }

    /** One submission path: prepare the ledger request, prompt for elevation and observe the result. */
    private fun submit(active: SessionState.Active, kind: InstallationKind, request: InstallationRequest) {
        val installs = installations ?: return
        val provider = elevationAnswers ?: return
        action?.cancel()
        action = viewModelScope.launch {
            mutable.value = mutable.value.copy(busy = true, actionProblem = null)
            val result = try {
                val intent = installs.prepare(active, kind, request)
                installs.submit(intent, provider)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: IllegalArgumentException) {
                ApiResult.Problem(409, "installation.not_supported", null)
            } catch (_: Exception) {
                ApiResult.Transport(null)
            }
            if (session.state.value !== active) return@launch
            mutable.value = when (result) {
                is ApiResult.Success -> mutable.value.copy(busy = false, operation = ComponentOperationNotice(result.value.operationId))
                is ApiResult.Problem -> mutable.value.copy(busy = false, actionProblem = result.code ?: "installation.failed")
                is ApiResult.Transport -> mutable.value.copy(busy = false, actionProblem = "installation.unreachable")
            }
            if (result is ApiResult.Success) load(active)
        }
    }

    /**
     * Removes a managed Nginx from the component center. The request enters the same Nginx
     * installation ledger the application used, so approval, elevation and recovery do not change.
     */
    fun uninstallNginx(active: SessionState.Active, componentId: String) {
        if (!validNginxUninstall(componentId) || mutable.value.busy || session.state.value !== active) return
        submit(active, InstallationKind.Uninstall, NginxInstallationRequest(true))
    }

    fun dismissReview() { mutable.value = mutable.value.copy(review = null) }

    fun dismissOperation() { mutable.value = mutable.value.copy(operation = null) }

    fun dismissActionProblem() { mutable.value = mutable.value.copy(actionProblem = null) }

    /** Loads the host-global Nginx selection together with the managed/integrated instances. */
    fun openNginxSelection(active: SessionState.Active) {
        if (session.state.value !== active) return
        action?.cancel()
        action = viewModelScope.launch {
            mutable.value = mutable.value.copy(nginxLoading = true)
            val current = try { repository.currentNginx(active) } catch (_: Exception) { ApiResult.Transport(null) }
            if (session.state.value !== active) return@launch
            if (current !is ApiResult.Success) {
                mutable.value = mutable.value.copy(nginxLoading = false, actionProblem = (current as? ApiResult.Problem)?.code ?: "component.nginx_unavailable")
                return@launch
            }
            val servers = try { repository.nginxServers(active) } catch (_: Exception) { ApiResult.Transport(null) }
            if (session.state.value !== active) return@launch
            mutable.value = when (servers) {
                is ApiResult.Success -> mutable.value.copy(nginxLoading = false,
                    nginx = NginxChoiceState(current.value, servers.value.filter { it.providerId == "nginx" }))
                is ApiResult.Problem -> mutable.value.copy(nginxLoading = false, actionProblem = servers.code ?: "component.nginx_unavailable")
                is ApiResult.Transport -> mutable.value.copy(nginxLoading = false, actionProblem = "component.nginx_unavailable")
            }
        }
    }

    fun selectNginx(active: SessionState.Active, serverId: String) {
        if (session.state.value !== active || mutable.value.nginx == null) return
        action?.cancel()
        action = viewModelScope.launch {
            mutable.value = mutable.value.copy(busy = true, actionProblem = null)
            val result = try { repository.selectCurrentNginx(active, serverId) } catch (_: Exception) { ApiResult.Transport(null) }
            if (session.state.value !== active) return@launch
            mutable.value = when (result) {
                is ApiResult.Success -> mutable.value.copy(busy = false,
                    nginx = mutable.value.nginx?.copy(current = result.value))
                is ApiResult.Problem -> mutable.value.copy(busy = false, actionProblem = result.code ?: "component.nginx_unavailable")
                is ApiResult.Transport -> mutable.value.copy(busy = false, actionProblem = "component.nginx_unavailable")
            }
        }
    }

    fun closeNginxSelection() { mutable.value = mutable.value.copy(nginx = null) }
}
