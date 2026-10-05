package app.relaxkonos.mobile.ui.manage.git

import androidx.compose.runtime.*
import app.relaxkonos.mobile.data.GitRepositoryClient
import app.relaxkonos.mobile.data.DeploymentRepository
import androidx.compose.runtime.saveable.listSaver
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.*
import java.util.UUID

/** Remote build workflow; Compose only renders its state and dispatches actions. */
internal class GitBuildEditor(
    private val git: GitRepositoryClient,
    private val deployments: DeploymentRepository,
    private val owner: SessionState.Active,
    private val isCurrentOwner: () -> Boolean,
    parentScope: CoroutineScope,
) {
    private val job = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + job)
    private var poll: Job? = null
    private var initialBuildId: String? = null
    private val current get() = job.isActive && isCurrentOwner()
    var url by mutableStateOf("")
    var reference by mutableStateOf("main")
    var context by mutableStateOf(".")
    var dockerfile by mutableStateOf("Dockerfile")
    var credentialName by mutableStateOf("")
    var credentialToken by mutableStateOf("")
    var credentialId by mutableStateOf<String?>(null)
    var credentials by mutableStateOf<List<GitBuildCredential>>(emptyList())
    var remoteRefs by mutableStateOf<List<GitBuildRef>>(emptyList())
    var resolved by mutableStateOf<GitBuildResolved?>(null)
    var lastResolvedSha by mutableStateOf<String?>(null)
    var buildKey by mutableStateOf<String?>(null)
    var builds by mutableStateOf<List<GitBuildOperation>>(emptyList())
    var selectedBuildId by mutableStateOf<String?>(null)
    var appliedInitialBuildId by mutableStateOf<String?>(null)
    var applications by mutableStateOf<List<DeploymentApplication>>(emptyList())
    var applicationId by mutableStateOf<String?>(null)
    var applicationName by mutableStateOf("")
    var containerPort by mutableStateOf("8080")
    var definitionKey by mutableStateOf<String?>(null)
    var publishKey by mutableStateOf<String?>(null)
    var active by mutableStateOf(false)
    var problem by mutableStateOf<String?>(null)
    var notice by mutableStateOf<String?>(null)
    val selected get() = builds.firstOrNull { it.id == selectedBuildId }

    fun changeUrl(value: String) { url = value; invalidateSource(); remoteRefs = emptyList() }
    fun changeReference(value: String) { reference = value; invalidateSource() }
    fun selectCredential(id: String?) { credentialId = id; invalidateSource(); remoteRefs = emptyList() }
    fun changeContext(value: String) { context = value; buildKey = null }
    fun changeDockerfile(value: String) { dockerfile = value; buildKey = null }
    fun selectBuild(id: String) { selectedBuildId = id; publishKey = null; problem = null }
    fun selectApplication(id: String?) { applicationId = id; publishKey = null }
    fun changeApplicationName(value: String) { applicationName = value; definitionKey = null }
    fun changeContainerPort(value: String) { containerPort = value; definitionKey = null }
    private fun invalidateSource() { resolved = null; buildKey = null }

    private fun report(result: ApiResult<*>) {
        problem = when (result) {
            is ApiResult.Problem -> result.code
            is ApiResult.Transport -> "transport"
            else -> null
        }
    }

    fun close() { job.cancel(); credentialToken = "" }

    fun open(id: String?) {
        initialBuildId = id
        applyInitialSelection()
    }

    private fun applyInitialSelection() {
        val id = initialBuildId ?: return
        if (appliedInitialBuildId != id && builds.any { it.id == id }) {
            selectedBuildId = id
            appliedInitialBuildId = id
        }
    }

    private suspend fun <T> owned(action: suspend () -> ApiResult<T>): ApiResult<T> {
        val result = action()
        currentCoroutineContext().ensureActive()
        if (!current) throw CancellationException("Git build owner changed")
        return result
    }

    private fun execute(action: suspend () -> Unit) {
        if (!current || active) return
        active = true
        scope.launch {
            try { action() }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current) problem = "transport" }
            finally { active = false }
        }
    }

    fun observeSelected() {
        poll?.cancel()
        val id = selectedBuildId ?: return
        poll = scope.launch {
            while (current && selected?.id == id && selected?.state in setOf("queued", "running")) {
                delay(2500)
                when (val result = owned { git.build(owner, id) }) {
                    is ApiResult.Success -> if (current) builds = builds.map { if (it.id == id) result.value else it }
                    else -> break
                }
            }
        }
    }

    fun load() = execute {
        when (val result = owned { git.credentials(owner) }) {
            is ApiResult.Success -> credentials = result.value
            else -> report(result)
        }
        when (val result = owned { git.builds(owner) }) {
            is ApiResult.Success -> {
                builds = result.value
                selectedBuildId = selectedBuildId?.takeIf { id -> builds.any { it.id == id } } ?: builds.firstOrNull()?.id
                applyInitialSelection()
            }
            else -> report(result)
        }
        when (val result = owned { deployments.applications(owner) }) {
            is ApiResult.Success -> applications = result.value.filter { it.sourceKind == "image" }
            else -> Unit
        }
        applyInitialSelection()
    }

    fun refresh() = execute {
        when (val result = owned { git.builds(owner) }) {
            is ApiResult.Success -> {
                builds = result.value
                if (selectedBuildId == null) selectedBuildId = builds.firstOrNull()?.id
                applyInitialSelection()
            }
            else -> report(result)
        }
        when (val result = owned { deployments.applications(owner) }) {
            is ApiResult.Success -> applications = result.value.filter { it.sourceKind == "image" }
            else -> Unit
        }
        applyInitialSelection()
    }

    fun listRefs() = execute {
        problem = null
        when (val result = owned { git.refs(owner, url, credentialId) }) {
            is ApiResult.Success -> remoteRefs = result.value
            else -> report(result)
        }
    }

    fun saveCredential() = execute {
        problem = null
        when (val result = owned { git.setCredential(owner, credentialName, credentialToken) }) {
            is ApiResult.Success -> {
                credentials = credentials + result.value
                credentialId = result.value.id
                credentialToken = ""
                credentialName = ""
                resolved = null
                buildKey = null
            }
            else -> report(result)
        }
    }

    fun resolve() = execute {
        problem = null
        when (val result = owned { git.resolve(owner, url, reference, credentialId) }) {
            is ApiResult.Success -> {
                if (lastResolvedSha != result.value.commitSha) buildKey = null
                resolved = result.value
                lastResolvedSha = result.value.commitSha
            }
            else -> report(result)
        }
    }

    fun startBuild(fixed: GitBuildResolved) = execute {
        problem = null
        notice = null
        val key = buildKey ?: UUID.randomUUID().toString().also { buildKey = it }
        val request = GitBuildRequest(fixed.repositoryUrl, fixed.reference, fixed.commitSha,
            context.trim(), dockerfile.trim(), credentialId)
        when (val result = owned { git.startBuild(owner, request, key) }) {
            is ApiResult.Success -> {
                builds = (listOf(result.value) + builds).distinctBy { it.id }
                selectedBuildId = result.value.id
                publishKey = null
            }
            else -> report(result)
        }
    }

    fun cancelBuild(build: GitBuildOperation) = execute {
        when (val result = owned { git.cancelBuild(owner, build.id) }) {
            is ApiResult.Success -> builds = builds.map { if (it.id == build.id) result.value else it }
            else -> report(result)
        }
    }

    fun publish(build: GitBuildOperation) = execute {
        problem = null
        notice = null
        val target = applicationId ?: run {
            val key = definitionKey ?: UUID.randomUUID().toString().also { definitionKey = it }
            val definition = ImageDeploymentDefinition(applicationName, containerPort.toInt())
            when (val created = owned { deployments.createImageDefinition(owner, definition, key) }) {
                is ApiResult.Success -> {
                    applications = applications + created.value
                    applicationId = created.value.id
                    created.value.id
                }
                else -> { report(created); null }
            }
        }
        if (target != null) {
            val key = publishKey ?: UUID.randomUUID().toString().also { publishKey = it }
            when (val result = owned { deployments.deployGitBuild(owner, target, build, key) }) {
                is ApiResult.Success -> notice = result.value.operationId
                else -> report(result)
            }
        }
    }

    companion object {
        fun saver(git: GitRepositoryClient, deployments: DeploymentRepository, owner: SessionState.Active,
            isCurrentOwner: () -> Boolean, scope: CoroutineScope) = listSaver<GitBuildEditor, Any?>(
            save = { listOf(it.url, it.reference, it.context, it.dockerfile, it.credentialName, it.credentialId, it.lastResolvedSha, it.buildKey, it.selectedBuildId, it.applicationId, it.applicationName, it.containerPort, it.definitionKey, it.publishKey) },
            restore = { saved ->
                GitBuildEditor(git, deployments, owner, isCurrentOwner, scope).apply {
                    url = saved[0] as String
                    reference = saved[1] as String
                    context = saved[2] as String
                    dockerfile = saved[3] as String
                    credentialName = saved[4] as String
                    credentialId = saved[5] as String?
                    lastResolvedSha = saved[6] as String?
                    buildKey = saved[7] as String?
                    selectedBuildId = saved[8] as String?
                    applicationId = saved[9] as String?
                    applicationName = saved[10] as String
                    containerPort = saved[11] as String
                    definitionKey = saved[12] as String?
                    publishKey = saved[13] as String?
                }
            },
        )
    }
}
