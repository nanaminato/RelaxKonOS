package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.OutboundProxySettings
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DockerContainer
import app.relaxkonos.mobile.core.net.DockerImage
import app.relaxkonos.mobile.core.net.DockerLogs
import app.relaxkonos.mobile.core.net.DockerNetwork
import app.relaxkonos.mobile.core.net.DockerOperation
import app.relaxkonos.mobile.core.net.DockerStack
import app.relaxkonos.mobile.core.net.DockerStackOperation
import app.relaxkonos.mobile.core.net.DockerStackOperationDiagnostics
import app.relaxkonos.mobile.core.net.DockerStackPreview
import app.relaxkonos.mobile.core.net.DockerStackService
import app.relaxkonos.mobile.core.net.DockerStatus
import app.relaxkonos.mobile.core.net.DockerVolume
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** Session-scoped Docker facade. It serializes refresh-token retries and never talks to an Engine directly. */
class DockerRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
    private val operationIndex: OperationIndex,
    private val gate: DockerMutationGate,
) {
    private val reads = Mutex()
    suspend fun proxyStatus(owner: SessionState.Active) = read(owner) { url, token -> gateway.outboundProxyStatus(url, token) }
    suspend fun saveProxy(owner: SessionState.Active, settings: OutboundProxySettings, confirmed: Boolean) =
        read(owner) { url, token -> gateway.saveOutboundProxy(url, token, settings, confirmed) }
    suspend fun clearProxy(owner: SessionState.Active) = read(owner) { url, token -> gateway.clearOutboundProxy(url, token) }
    suspend fun status(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerStatus(url, token) }
    suspend fun containers(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerContainers(url, token) }
    suspend fun images(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerImages(url, token) }
    suspend fun networks(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerNetworks(url, token) }
    suspend fun volumes(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerVolumes(url, token) }
    /** The reference list the operator has to see before deciding to release a volume's data. */
    suspend fun volumeDetails(owner: SessionState.Active, name: String) = read(owner) { url, token -> gateway.dockerVolumeDetails(url, token, name) }
    /** Releases a volume's data. The server refuses while a container still references it. */
    suspend fun stacks(owner: SessionState.Active) = read(owner) { url, token -> gateway.dockerStacks(url, token) }
    suspend fun services(owner: SessionState.Active, name: String) = read(owner) { url, token -> gateway.dockerStackServices(url, token, name) }
    suspend fun logs(owner: SessionState.Active, id: String, tail: Int) = read(owner) { url, token -> gateway.dockerContainerLogs(url, token, id, tail) }

    /** Parses a definition without applying it. The answer is what the operator approves. */
    suspend fun previewStack(owner: SessionState.Active, name: String, composeYaml: String): ApiResult<DockerStackPreview> =
        read(owner) { url, token -> gateway.dockerStackPreview(url, token, name, composeYaml) }

    /**
     * Submits a deployment bound to the approved definition version. The key is generated per
     * submission so a retried request returns the operation that was already created instead of
     * starting a second deployment of the same stack.
     */
    suspend fun deployStack(owner: SessionState.Active, name: String, composeYaml: String, definitionVersion: String): ApiResult<DockerStackOperation> {
        val key = UUID.randomUUID().toString()
        return mutate(owner) { url, token -> gateway.dockerStackDeploy(url, token, name, composeYaml, definitionVersion, key) }
    }

    suspend fun stackAction(owner: SessionState.Active, name: String, action: String, confirmed: Boolean): ApiResult<DockerStackOperation> {
        val key = UUID.randomUUID().toString()
        return mutate(owner) { url, token -> gateway.dockerStackAction(url, token, name, action, confirmed, key) }
    }

    suspend fun stackOperations(owner: SessionState.Active, name: String, limit: Int = 20): ApiResult<List<DockerStackOperation>> =
        read(owner) { url, token -> gateway.dockerStackOperations(url, token, name, limit) }

    suspend fun stackOperation(owner: SessionState.Active, operationId: String): ApiResult<DockerStackOperation> =
        read(owner) { url, token -> gateway.dockerStackOperation(url, token, operationId) }

    suspend fun stackOperationDiagnostics(owner: SessionState.Active, operationId: String): ApiResult<DockerStackOperationDiagnostics> =
        read(owner) { url, token -> gateway.dockerStackOperationDiagnostics(url, token, operationId) }

    suspend fun cancelStackOperation(owner: SessionState.Active, operationId: String): ApiResult<DockerStackOperation> {
        val key = UUID.nameUUIDFromBytes("cancel:${owner.serviceId}:${owner.userName}:$operationId".toByteArray(Charsets.UTF_8)).toString()
        return read(owner) { url, token -> gateway.dockerStackOperationCancel(url, token, operationId, key) }
    }

    private suspend fun mutate(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<DockerStackOperation>): ApiResult<DockerStackOperation> = gate.mutex.withLock {
        if (session.state.value !== owner) throw CancellationException("Docker session changed")
        val allowed = gate.check(owner)
        when (allowed) { is ApiResult.Problem -> allowed; is ApiResult.Transport -> allowed; is ApiResult.Success -> read(owner, call) }
    }
    private suspend fun <T> read(owner: SessionState.Active, call: suspend (String, String) -> ApiResult<T>): ApiResult<T> = reads.withLock {
        fun verifyOwner() { if (session.state.value !== owner) throw CancellationException("Docker session changed") }
        verifyOwner()
        val result = session.authenticated { url, token -> verifyOwner(); call(url, token) }
        verifyOwner()
        val operation = (result as? ApiResult.Success)?.value as? DockerStackOperation
        if (operation != null) runCatching {
            operationIndex.record(owner, OperationDomain.Compose, operation.projectName, operation.operationId)
        }
        result
    }
}
