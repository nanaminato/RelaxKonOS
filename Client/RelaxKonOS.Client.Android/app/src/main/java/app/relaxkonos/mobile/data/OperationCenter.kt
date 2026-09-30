package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.InstallationOperation
import app.relaxkonos.mobile.core.net.InstallationService
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.BackupManifest
import app.relaxkonos.mobile.core.net.DeploymentOperation
import app.relaxkonos.mobile.core.net.DockerStackOperation
import app.relaxkonos.mobile.core.net.GitBuildOperation
import app.relaxkonos.mobile.core.net.ScriptTask
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.core.net.WebsitePublicationOperation
import kotlinx.coroutines.CancellationException

enum class OperationCheck { Verified, Unavailable, Missing }

data class ObservedOperation(
    val reference: OperationReference,
    val check: OperationCheck,
    val target: String,
    val state: String? = null,
    val stage: String? = null,
    val progress: Int? = null,
    val problemCode: String? = null,
    val cancellable: Boolean = false,
    val checkedAtMillis: Long? = null,
    val installation: InstallationOperation? = null,
    val webServer: app.relaxkonos.mobile.core.net.WebServerOperation? = null,
    val certificate: app.relaxkonos.mobile.core.net.CertificateOperation? = null,
)

data class OperationCenterSnapshot(val items: List<ObservedOperation>, val incomplete: Boolean,
    val pendingInstallations: List<PendingInstallationRequest> = emptyList(),
    val pendingSites: List<PendingSiteMutation> = emptyList(),
    val pendingCertificates: List<PendingCertificateRequest> = emptyList(),
    val pendingTunnels: List<PendingTunnelMutation> = emptyList())

/** Reads each domain's own durable record. Discovery also recovers operations not yet in the local index. */
class OperationCenter(
    private val session: AuthSession,
    private val index: OperationIndex,
    private val deployments: DeploymentRepository,
    private val websites: WebPublishingRepository,
    private val docker: DockerRepository,
    private val git: GitRepositoryClient,
    private val scripts: ScriptTaskRepository,
    private val backups: BackupRecoveryRepository,
    private val installations: InstallationRepository,
    private val webServers: WebServerRepository,
    private val webSites: WebSiteRepository,
    private val certificates: CertificateRepository,
    private val tunnels: TunnelRepository,
) {
    suspend fun refresh(owner: SessionState.Active): OperationCenterSnapshot {
        verify(owner)
        val discovered = linkedMapOf<Pair<OperationDomain, String>, ObservedOperation>()
        var incomplete = false
        val applications = if (ServerCapabilities.APPLICATION_DEPLOYMENTS in owner.capabilities) deployments.applications(owner) else null
        if (applications != null && applications !is ApiResult.Success) incomplete = true
        if (applications is ApiResult.Success) {
            if (applications.value.size > 100) incomplete = true
            applications.value.take(100).forEach { application ->
                verify(owner)
                val snapshot = deployments.snapshot(owner, application.id)
                if (snapshot !is ApiResult.Success) incomplete = true
                if (snapshot is ApiResult.Success) (snapshot.value.operations + listOfNotNull(snapshot.value.activeOperation))
                    .distinctBy { it.operationId }.forEach operationLoop@ { operation ->
                    if (index.isHidden(owner, OperationDomain.Deployment, operation.operationId)) return@operationLoop
                    runCatching { index.record(owner, OperationDomain.Deployment, application.id, operation.operationId) }
                    val observed = fromDeployment(owner, operation, application.name)
                    discovered[OperationDomain.Deployment to operation.operationId] = observed
                }
                if (ServerCapabilities.WEB_SERVER in owner.capabilities) {
                    val history = websites.history(owner, application.id)
                    if (history !is ApiResult.Success) incomplete = true
                    if (history is ApiResult.Success) history.value.forEach publicationLoop@ { operation ->
                        if (index.isHidden(owner, OperationDomain.Website, operation.operationId)) return@publicationLoop
                        runCatching { index.record(owner, OperationDomain.Website, application.id, operation.operationId) }
                        discovered[OperationDomain.Website to operation.operationId] = fromWebsite(owner, operation, application.name)
                    }
                }
                if (ServerCapabilities.BACKUP_RECOVERY in owner.capabilities) {
                    val manifests = backups.manifests(owner, application.id)
                    if (manifests !is ApiResult.Success) incomplete = true
                    if (manifests is ApiResult.Success) manifests.value.forEach backupLoop@ { backup ->
                        if (index.isHidden(owner, OperationDomain.Backup, backup.backupId)) return@backupLoop
                        runCatching { index.record(owner, OperationDomain.Backup, application.id, backup.backupId) }
                        discovered[OperationDomain.Backup to backup.backupId] = fromBackup(owner, backup, application.name)
                    }
                }
            }
        }
        if (ServerCapabilities.DOCKER in owner.capabilities) {
            val stacks = docker.stacks(owner)
            if (stacks !is ApiResult.Success) incomplete = true
            if (stacks is ApiResult.Success) {
                if (stacks.value.size > 100) incomplete = true
                stacks.value.take(100).forEach { stack ->
                    verify(owner)
                    val operations = docker.stackOperations(owner, stack.name, 100)
                    if (operations !is ApiResult.Success) incomplete = true
                    if (operations is ApiResult.Success) operations.value.forEach operationLoop@ { operation ->
                        if (index.isHidden(owner, OperationDomain.Compose, operation.operationId)) return@operationLoop
                        runCatching { index.record(owner, OperationDomain.Compose, stack.name, operation.operationId) }
                        discovered[OperationDomain.Compose to operation.operationId] = fromCompose(owner, operation)
                    }
                }
            }
        }
        if (ServerCapabilities.GIT in owner.capabilities) {
            val builds = git.builds(owner)
            if (builds !is ApiResult.Success) incomplete = true
            if (builds is ApiResult.Success) builds.value.forEach buildLoop@ { build ->
                if (index.isHidden(owner, OperationDomain.GitBuild, build.id)) return@buildLoop
                runCatching { index.record(owner, OperationDomain.GitBuild, build.id, build.id) }
                discovered[OperationDomain.GitBuild to build.id] = fromGitBuild(owner, build)
            }
        }
        if (ServerCapabilities.GUARDIAN in owner.capabilities) {
            val tasks = scripts.tasks(owner)
            if (tasks !is ApiResult.Success || !tasks.value.success) incomplete = true
            if (tasks is ApiResult.Success && tasks.value.success) tasks.value.tasks.forEach scriptLoop@ { task ->
                if (index.isHidden(owner, OperationDomain.Script, task.id)) return@scriptLoop
                runCatching { index.record(owner, OperationDomain.Script, task.id, task.id) }
                discovered[OperationDomain.Script to task.id] = fromScript(owner, task)
            }
        }
        val pendingInstallations = installations.pending(owner)
        pendingInstallations.filter { it.operationId != null }.forEach { pending ->
            val result = installations.recover(owner, pending)
            if (result !is ApiResult.Success) incomplete = true
        }
        InstallationService.entries.filter { owner.privilegedOperations && it.capability in owner.capabilities }.forEach { service ->
            verify(owner)
            when (val result = installations.active(owner, service)) {
                is ApiResult.Success -> result.value?.let { operation ->
                    if (!index.isHidden(owner, OperationDomain.Installation, operation.operationId)) {
                        discovered[OperationDomain.Installation to operation.operationId] = fromInstallation(owner, operation)
                    }
                }
                else -> incomplete = true
            }
        }
        if (ServerCapabilities.WEB_SERVER in owner.capabilities) webServers.pending(owner).mapNotNull { it.operationId }.forEach { id ->
            val result = webServers.operation(owner, id)
            if (result !is ApiResult.Success) incomplete = true
        }
        if (ServerCapabilities.CERTIFICATES in owner.capabilities) certificates.pending(owner).mapNotNull { it.operationId }.forEach { id ->
            if (certificates.operation(owner, id) !is ApiResult.Success) incomplete = true
        }
        index.forOwner(owner).forEach { reference ->
            verify(owner)
            val key = reference.domain to reference.operationId
            if (key !in discovered) discovered[key] = query(owner, reference)
        }
        verify(owner)
        return OperationCenterSnapshot(discovered.values.sortedByDescending { it.reference.seenAtMillis }, incomplete, installations.pending(owner).filter { it.attempted }, webSites.pending(owner).filter { it.attempted }, certificates.pending(owner).filter { it.attempted },
            if (ServerCapabilities.TUNNELS in owner.capabilities) tunnels.pending(owner) else emptyList())
    }

    suspend fun diagnostics(owner: SessionState.Active, item: ObservedOperation): ApiResult<List<String>> {
        verify(owner)
        return when (item.reference.domain) {
            OperationDomain.Deployment -> when (val result = deployments.operationDiagnostics(owner, item.reference.operationId)) {
                is ApiResult.Success -> ApiResult.Success(result.value.lines)
                is ApiResult.Problem -> result
                is ApiResult.Transport -> result
            }
            OperationDomain.Compose -> when (val result = docker.stackOperationDiagnostics(owner, item.reference.operationId)) {
                is ApiResult.Success -> ApiResult.Success(result.value.lines)
                is ApiResult.Problem -> result
                is ApiResult.Transport -> result
            }
            OperationDomain.GitBuild -> when (val result = git.build(owner, item.reference.operationId)) {
                is ApiResult.Success -> ApiResult.Success(result.value.logs)
                is ApiResult.Problem -> result
                is ApiResult.Transport -> result
            }
            OperationDomain.Script -> when (val result = scripts.task(owner, item.reference.operationId)) {
                is ApiResult.Success -> {
                    val task = result.value.task
                    if (result.value.success && task?.id == item.reference.operationId) {
                        ApiResult.Success(task.output.map { it.text })
                    } else {
                        ApiResult.Transport(result.value.problemCode)
                    }
                }
                is ApiResult.Problem -> result
                is ApiResult.Transport -> result
            }
            else -> ApiResult.Success(emptyList())
        }
    }

    suspend fun cancel(owner: SessionState.Active, item: ObservedOperation): ApiResult<*> {
        verify(owner)
        if (!item.cancellable) {
            return ApiResult.Transport("Cancellation is unavailable for this operation.")
        }
        return when (item.reference.domain) {
            OperationDomain.Deployment -> {
                val key = java.util.UUID.nameUUIDFromBytes(
                    "cancel:${owner.serviceId}:${owner.userName}:${item.reference.operationId}".toByteArray(Charsets.UTF_8)).toString()
                deployments.cancel(owner, item.reference.operationId, key)
            }
            OperationDomain.Certificate -> certificates.cancel(owner, item.reference.operationId)
            OperationDomain.WebServer -> webServers.cancel(owner, item.reference.operationId)
            OperationDomain.Installation -> installations.cancel(owner, item.reference.operationId)
            OperationDomain.Compose -> docker.cancelStackOperation(owner, item.reference.operationId)
            OperationDomain.GitBuild -> git.cancelBuild(owner, item.reference.operationId)
            OperationDomain.Script -> when (val result = scripts.cancel(owner, item.reference.operationId)) {
                is ApiResult.Success -> if (result.value.success) result else ApiResult.Transport(result.value.problemCode)
                else -> result
            }
            else -> ApiResult.Transport("Cancellation is unavailable for this operation.")
        }
    }

    fun hide(owner: SessionState.Active, item: ObservedOperation) = index.hide(owner, item.reference)

    private suspend fun query(owner: SessionState.Active, reference: OperationReference): ObservedOperation =
        when (reference.domain) {
            OperationDomain.Deployment -> when (val result = deployments.operation(owner, reference.operationId)) {
                is ApiResult.Success -> if (result.value.applicationId == reference.resourceId)
                    fromDeployment(owner, result.value, reference.resourceId) else unknown(reference, OperationCheck.Missing)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
            OperationDomain.Certificate -> when (val result = certificates.operation(owner, reference.operationId)) {
                is ApiResult.Success -> if (result.value.certificateId == reference.resourceId) fromCertificate(owner, result.value) else unknown(reference, OperationCheck.Missing)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
            OperationDomain.WebServer -> when (val result = webServers.operation(owner, reference.operationId)) {
                is ApiResult.Success -> if (result.value.instanceId == reference.resourceId)
                    verified(owner, OperationDomain.WebServer, result.value.instanceId, result.value.operationId,
                        result.value.instanceId, result.value.state.wire, result.value.stage, null,
                        result.value.problemCode.takeIf(String::isNotBlank), result.value.state.active).copy(webServer = result.value)
                    else unknown(reference, OperationCheck.Missing)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
            OperationDomain.Website -> when (val result = websites.operation(owner, reference.operationId)) {
                is ApiResult.Success -> if (result.value.applicationId == reference.resourceId) fromWebsite(owner, result.value, result.value.domain)
                    else unknown(reference, OperationCheck.Missing)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
            OperationDomain.Compose -> when (val result = docker.stackOperation(owner, reference.operationId)) {
                is ApiResult.Success -> if (result.value.projectName == reference.resourceId) fromCompose(owner, result.value)
                    else unknown(reference, OperationCheck.Missing)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
            OperationDomain.GitBuild -> when (val result = git.build(owner, reference.operationId)) {
                is ApiResult.Success -> if (result.value.id == reference.resourceId) fromGitBuild(owner, result.value)
                    else unknown(reference, OperationCheck.Missing)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
            OperationDomain.Script -> when (val result = scripts.task(owner, reference.operationId)) {
                is ApiResult.Success -> result.value.task?.takeIf { result.value.success && it.id == reference.resourceId }
                    ?.let { fromScript(owner, it) } ?: unknown(reference,
                        if (result.value.problemCode == "guardian.script_not_found") OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
            OperationDomain.Installation -> when (val result = installations.operation(owner, reference.operationId)) {
                is ApiResult.Success -> if (result.value.service.name == reference.resourceId)
                    fromInstallation(owner, result.value) else unknown(reference, OperationCheck.Missing)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
            OperationDomain.Backup -> when (val result = backups.manifest(owner, reference.operationId)) {
                is ApiResult.Success -> if (result.value.applicationId == reference.resourceId)
                    fromBackup(owner, result.value, reference.resourceId) else unknown(reference, OperationCheck.Missing)
                is ApiResult.Problem -> unknown(reference, if (result.status == 404) OperationCheck.Missing else OperationCheck.Unavailable)
                is ApiResult.Transport -> unknown(reference, OperationCheck.Unavailable)
            }
        }

    private fun fromCertificate(owner: SessionState.Active, operation: app.relaxkonos.mobile.core.net.CertificateOperation): ObservedOperation =
        verified(owner, OperationDomain.Certificate, requireNotNull(operation.certificateId), operation.operationId,
            requireNotNull(operation.certificateId), operation.state.wire, operation.stage, null,
            operation.problemCode.takeIf(String::isNotBlank), operation.state.active).copy(certificate = operation)

    private fun fromDeployment(owner: SessionState.Active, operation: DeploymentOperation, target: String): ObservedOperation =
        verified(owner, OperationDomain.Deployment, operation.applicationId, operation.operationId,
            target, operation.state, operation.stage, operation.progress,
            operation.problemCode ?: operation.recoveryProblemCode, operation.cancellable)

    private fun fromWebsite(owner: SessionState.Active, operation: WebsitePublicationOperation, target: String): ObservedOperation =
        verified(owner, OperationDomain.Website, operation.applicationId, operation.operationId,
            target, operation.state, operation.stage, null,
            operation.problemCode.takeIf { it.isNotBlank() } ?: operation.recoveryProblemCode)

    private fun fromCompose(owner: SessionState.Active, operation: DockerStackOperation): ObservedOperation =
        verified(owner, OperationDomain.Compose, operation.projectName, operation.operationId,
            operation.projectName, operation.state.wire, operation.stage, null,
            operation.problemCode ?: operation.recoveryProblemCode, operation.cancellable)

    private fun fromGitBuild(owner: SessionState.Active, build: GitBuildOperation): ObservedOperation =
        verified(owner, OperationDomain.GitBuild, build.id, build.id,
            build.reference, build.state, "", null, build.problemCode,
            build.state == "queued" || build.state == "running")

    private fun fromScript(owner: SessionState.Active, task: ScriptTask): ObservedOperation =
        verified(owner, OperationDomain.Script, task.id, task.id,
            task.executablePath.substringAfterLast('/').substringAfterLast('\\'), task.state, "", null, task.problemCode,
            task.state == "queued" || task.state == "running")

    private fun fromInstallation(owner: SessionState.Active, operation: InstallationOperation): ObservedOperation =
        verified(owner, OperationDomain.Installation, operation.service.name, operation.operationId,
            operation.service.name, operation.state.wire, operation.stage.wire, operation.progress,
            operation.problemCode, operation.cancellable && operation.state.active).copy(installation = operation)

    private fun fromBackup(owner: SessionState.Active, backup: BackupManifest, target: String): ObservedOperation =
        verified(owner, OperationDomain.Backup, backup.applicationId, backup.backupId,
            target, backup.state, "backup", null, backup.problemCode)

    private fun verified(owner: SessionState.Active, domain: OperationDomain, resourceId: String, operationId: String,
        target: String, state: String, stage: String, progress: Int?, problemCode: String?, cancellable: Boolean = false): ObservedOperation {
        val checkedAt = System.currentTimeMillis()
        runCatching { index.markVerified(owner, domain, operationId, checkedAt) }
        return ObservedOperation(reference(owner, domain, resourceId, operationId), OperationCheck.Verified,
            target, state, stage, progress, problemCode, cancellable, checkedAt)
    }

    private fun reference(owner: SessionState.Active, domain: OperationDomain, resourceId: String, operationId: String) =
        index.forOwner(owner).firstOrNull { it.domain == domain && it.operationId == operationId }
            ?: OperationReference(owner.serviceId, owner.userName, domain, resourceId, operationId, System.currentTimeMillis())

    private fun unknown(reference: OperationReference, check: OperationCheck) =
        ObservedOperation(reference, check, reference.resourceId,
            checkedAtMillis = reference.lastVerifiedAtMillis)

    private fun verify(owner: SessionState.Active) {
        if (session.state.value !== owner) throw CancellationException("Operation center session changed")
    }
}
