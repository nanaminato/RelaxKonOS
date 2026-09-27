package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DockerContainer
import app.relaxkonos.mobile.core.net.DockerImage
import app.relaxkonos.mobile.core.net.DockerLogs
import app.relaxkonos.mobile.core.net.DockerNetwork
import app.relaxkonos.mobile.core.net.DockerOperation
import app.relaxkonos.mobile.core.net.DockerStack
import app.relaxkonos.mobile.core.net.DockerStackService
import app.relaxkonos.mobile.core.net.DockerStatus
import app.relaxkonos.mobile.core.net.DockerVolume
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Session-scoped Docker facade. It serializes refresh-token retries and never talks to an Engine directly. */
class DockerRepository(private val gateway: RelaxKonGateway, private val session: AuthSession) {
    private val reads = Mutex()
    suspend fun status(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerStatus(url, token) }
    suspend fun containers(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerContainers(url, token) }
    suspend fun images(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerImages(url, token) }
    suspend fun networks(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerNetworks(url, token) }
    suspend fun volumes(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerVolumes(url, token) }
    suspend fun stacks(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerStacks(url, token) }
    suspend fun services(owner: SessionState.Active, name: String) = read(owner) { url, token -> gateway.dockerStackServices(url, token, name) }
    suspend fun logs(owner: SessionState.Active, id: String, tail: Int) = read(owner) { url, token -> gateway.dockerContainerLogs(url, token, id, tail) }
    suspend fun containerAction(owner: SessionState.Active, id: String, action: String, confirmed: Boolean) = read(owner) { url, token -> gateway.dockerContainerAction(url, token, id, action, confirmed) }
    suspend fun stackAction(owner: SessionState.Active, name: String, action: String, confirmed: Boolean) = read(owner) { url, token -> gateway.dockerStackAction(url, token, name, action, confirmed) }
    suspend fun deployStack(owner: SessionState.Active, name: String, composeYaml: String) = read(owner) { url, token -> gateway.dockerStackDefinition(url, token, name, composeYaml) }

    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> = reads.withLock {
        fun verifyOwner() { if (session.state.value !== owner) throw CancellationException("Docker session changed") }
        verifyOwner()
        val result = session.authenticated { url, token -> verifyOwner(); call(url, token) }
        verifyOwner()
        result
    }
}
