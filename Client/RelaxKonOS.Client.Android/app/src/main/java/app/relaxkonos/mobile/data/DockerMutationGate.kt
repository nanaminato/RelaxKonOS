package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.sync.Mutex

/** Shared serialization across engine, mirror, standalone resource and Compose submissions. */
class DockerMutationGate(private val control: DockerControlJournal, private val resources: DockerResourceJournal,
    private val installationCheck: suspend (SessionState.Active) -> ApiResult<Unit>) {
    val mutex = Mutex()
    suspend fun check(owner: SessionState.Active): ApiResult<Unit> {
        require(owner.privilegedOperations && ServerCapabilities.DOCKER in owner.capabilities)
        if (control.pending(owner).isNotEmpty() || resources.pending(owner).isNotEmpty())
            return ApiResult.Problem(409, "docker.resources.pending", null)
        return installationCheck(owner)
    }
}
