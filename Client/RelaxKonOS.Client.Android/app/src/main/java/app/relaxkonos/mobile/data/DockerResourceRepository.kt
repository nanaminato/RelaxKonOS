package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.withLock

class DockerResourceRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val journal: DockerResourceJournal, private val gate: DockerMutationGate) {
    fun pending(owner: SessionState.Active) = journal.pending(owner.also(::verify))
    suspend fun facts(owner: SessionState.Active): ApiResult<DockerResourceFacts> {
        val status = read(owner, gateway::dockerStatus); if (status !is ApiResult.Success) return failure(status)
        if (!status.value.available) return ApiResult.Success(DockerResourceFacts(status.value, null, null, null, null))
        val containers = read(owner, gateway::dockerContainers); if (containers !is ApiResult.Success) return failure(containers)
        val images = read(owner, gateway::dockerImages); if (images !is ApiResult.Success) return failure(images)
        val networks = read(owner, gateway::dockerNetworks); if (networks !is ApiResult.Success) return failure(networks)
        val volumes = read(owner, gateway::dockerVolumes); if (volumes !is ApiResult.Success) return failure(volumes)
        return ApiResult.Success(DockerResourceFacts(status.value, containers.value, images.value, networks.value, volumes.value))
    }
    suspend fun target(owner: SessionState.Active, kind: DockerResourceKind, id: String): ApiResult<DockerResourceTarget> = when (kind) {
        DockerResourceKind.Containers -> read(owner) { url, token -> gateway.dockerContainerDetails(url, token, id) }.map { detail ->
            require(DockerResourceValidation.identity(id, detail.id)); DockerResourceTarget(kind, id, container = detail)
        }
        DockerResourceKind.Networks -> read(owner) { url, token -> gateway.dockerNetworkDetails(url, token, id) }.map { detail ->
            require(DockerResourceValidation.identity(id, detail.id)); DockerResourceTarget(kind, id, network = detail)
        }
        DockerResourceKind.Volumes -> read(owner) { url, token -> gateway.dockerVolumeDetails(url, token, id) }.map { detail ->
            require(id == detail.name); DockerResourceTarget(kind, id, volume = detail)
        }
        DockerResourceKind.Images -> ApiResult.Success(DockerResourceTarget(kind, id.also { verify(owner); DockerResourceRoutes.segment(it) }))
    }
    suspend fun stats(owner: SessionState.Active, id: String): ApiResult<DockerContainerStats> =
        read(owner) { url, token -> gateway.dockerContainerStats(url, token, id) }.map { require(DockerResourceValidation.identity(it.containerId, id) || DockerResourceValidation.identity(id, it.containerId)); it }
    suspend fun logs(owner: SessionState.Active, id: String): ApiResult<DockerLogs> = read(owner) { url, token -> gateway.dockerContainerLogs(url, token, id, 200) }
    suspend fun change(owner: SessionState.Active, expected: DockerResourceFacts, target: DockerResourceTarget?, change: DockerResourceChange): ApiResult<DockerOperation> = gate.mutex.withLock {
        verify(owner)
        val available = gate.check(owner); if (available !is ApiResult.Success) return@withLock failure(available)
        validate(expected, target, change)
        val before = approved(owner, expected, target); if (before !is ApiResult.Success) return@withLock failure(before)
        val marker = journal.begin(owner, change)
        val result = session.authenticated { url, token ->
            verify(owner)
            val latest = approved(owner, expected, target); if (latest !is ApiResult.Success) return@authenticated failure<DockerOperation>(latest)
            gateway.dockerResourceChange(url, token, change)
        }
        verify(owner)
        if (result is ApiResult.Success && result.value.success) {
            val current = facts(owner)
            if (current is ApiResult.Success && current.value.status.available) journal.complete(marker)
        }
        result
    }
    suspend fun acceptFacts(owner: SessionState.Active, marker: PendingDockerResource): ApiResult<DockerResourceFacts> = gate.mutex.withLock {
        verify(owner); require(marker in journal.pending(owner))
        facts(owner).also { if (it is ApiResult.Success && it.value.status.available) journal.complete(marker) }
    }
    private suspend fun approved(owner: SessionState.Active, expected: DockerResourceFacts, target: DockerResourceTarget?): ApiResult<Unit> {
        val current = facts(owner); if (current !is ApiResult.Success) return failure(current)
        if (current.value.approvalFacts() != expected.approvalFacts()) return ApiResult.Problem(409, "docker.resources.facts_changed", null)
        if (target != null && target.kind != DockerResourceKind.Images) {
            val detail = this.target(owner, target.kind, target.id); if (detail !is ApiResult.Success) return failure(detail)
            if (detail.value != target) return ApiResult.Problem(409, "docker.resources.facts_changed", null)
        }
        return ApiResult.Success(Unit)
    }
    // CLI relative ages change while a confirmation is open; identity and state still gate writes.
    private fun DockerResourceFacts.approvalFacts() = copy(
        containers = containers?.map { it.copy(status = "") }?.sortedBy { it.id },
        images = images?.map { it.copy(createdSince = "") }?.sortedWith(compareBy({ it.id }, { it.repository }, { it.tag })),
        networks = networks?.sortedBy { it.id },
        volumes = volumes?.sortedBy { it.name },
    )
    private fun validate(facts: DockerResourceFacts, target: DockerResourceTarget?, change: DockerResourceChange) {
        require(facts.status.available && facts.containers != null && facts.images != null && facts.networks != null && facts.volumes != null)
        val action = change.action
        when (action) {
            DockerResourceAction.CreateContainer -> require(DockerResourceValidation.create(requireNotNull(change.container)))
            DockerResourceAction.PullImage -> require(DockerResourceValidation.image(requireNotNull(change.value)))
            DockerResourceAction.CreateNetwork, DockerResourceAction.CreateVolume -> {
                require(DockerResourceValidation.name(requireNotNull(change.value)) && DockerResourceValidation.name(requireNotNull(change.driver)))
                require(DockerResourceValidation.labels(change.labels))
            }
            else -> {
                require(target != null && target.id == change.target && !target.managed)
                when (action) {
                    DockerResourceAction.DeleteImage -> require(target.kind == DockerResourceKind.Images && facts.images.any { it.id == target.id })
                    DockerResourceAction.DeleteNetwork -> require(target.kind == DockerResourceKind.Networks && target.network != null && target.network.containers.isEmpty() && target.network.name !in setOf("bridge", "host", "none") && facts.networks.any { it.id == target.id })
                    DockerResourceAction.DeleteVolume -> require(target.kind == DockerResourceKind.Volumes && target.volume != null && target.volume.usedBy.isEmpty() && facts.volumes.any { it.name == target.id })
                    else -> {
                        require(target.kind == DockerResourceKind.Containers && target.container != null && facts.containers.any { it.id == target.id })
                        if (action == DockerResourceAction.RenameContainer) require(DockerResourceValidation.name(requireNotNull(change.value)))
                        else if (action == DockerResourceAction.Pause) require(target.container.state == "running")
                        else if (action == DockerResourceAction.Unpause) require(target.container.state == "paused")
                    }
                }
            }
        }
    }
    private suspend fun <T> read(owner: SessionState.Active, request: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner); return session.authenticated { url, token -> verify(owner); request(url, token) }.also { verify(owner) }
    }
    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Docker resource session changed")
        require(ServerCapabilities.DOCKER in owner.capabilities)
    }
    private fun <T, R> ApiResult<T>.map(transform: (T) -> R): ApiResult<R> = when (this) {
        is ApiResult.Success -> runCatching { transform(value) }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Mismatched Docker resource response") })
        else -> failure(this)
    }
    private fun <T> failure(result: ApiResult<*>): ApiResult<T> = when (result) {
        is ApiResult.Problem -> result; is ApiResult.Transport -> result; is ApiResult.Success -> error("Expected failed result")
    }
}
