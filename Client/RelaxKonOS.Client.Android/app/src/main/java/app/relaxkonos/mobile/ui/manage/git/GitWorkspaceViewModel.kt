package app.relaxkonos.mobile.ui.manage.git

import android.app.Application
import androidx.compose.runtime.*
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.compose.ui.text.input.TextFieldValue
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.withLock

internal data class GitWorkspaceState(val owner: SessionState.Active? = null, val busy: Boolean = false,
    val engine: GitEngine? = null, val repositories: List<GitRepository> = emptyList(), val selectedId: String? = null,
    val facts: GitWorkspaceFacts? = null, val pending: List<PendingGitMutation> = emptyList(), val preview: GitMutationPreview? = null,
    val diff: GitDiff? = null, val commits: List<GitCommit> = emptyList(), val skip: Int = 0, val search: String = "", val historyLoaded: Boolean = false,
    val detail: GitCommitDetail? = null, val conflict: GitConflictFile? = null, val problem: String? = null, val saved: Boolean = false,
    val installation: InstallationOperation? = null, val installationVerified: Boolean = false, val pendingInstallation: Boolean = false)
internal class GitWorkspaceViewModel(application: Application) : AndroidViewModel(application) {
    private val container get() = getApplication<RelaxKonApplication>().container
    private var job: Job? = null
    private var generation = 0
    private var installIntent: InstallationSubmission? = null
    var state by mutableStateOf(GitWorkspaceState()); private set
    var conflictDraft by mutableStateOf(TextFieldValue()); private set
    var commitMessage by mutableStateOf("")
    init { viewModelScope.launch { container.session.state.collect { next ->
        if (next !== state.owner) { job?.cancel(); generation++; installIntent = null; conflictDraft = TextFieldValue(); commitMessage = ""; state = GitWorkspaceState(owner = next as? SessionState.Active) }
    } } }
    fun stop() { job?.cancel(); generation++; state = state.copy(busy = false, preview = null, diff = null, detail = null, facts = null, installationVerified = false) }
    fun refresh() = work { owner ->
        val engine = container.gitWorkspace.engine(owner); verify(owner)
        state = state.copy(engine = (engine as? ApiResult.Success)?.value, problem = problem(engine))
        if (engine is ApiResult.Success && engine.value.available) {
            val repositories = container.git.repositories(owner); verify(owner)
            state = state.copy(repositories = (repositories as? ApiResult.Success)?.value.orEmpty(), problem = problem(repositories))
            val id = state.selectedId?.takeIf { target -> state.repositories.any { it.id == target } } ?: state.repositories.firstOrNull()?.id
            state = state.copy(selectedId = id)
            load(owner)
        } else state = state.copy(facts = null)
        loadInstallation(owner)
    }
    fun select(id: String) = work { owner ->
        require(state.repositories.any { it.id == id })
        state = state.copy(selectedId = id, facts = null, diff = null, commits = emptyList(), detail = null, conflict = null, historyLoaded = false, preview = null)
        load(owner)
    }
    fun register(name: String, path: String) = work { owner ->
        require(owner.executionEligibility.available)
        val result = container.git.register(owner, name, path); verify(owner)
        if (result is ApiResult.Success) {
            state = state.copy(repositories = (state.repositories + result.value).distinctBy { it.id }, selectedId = result.value.id, saved = true)
            load(owner)
        } else state = state.copy(problem = problem(result))
    }
    fun prepare(change: GitMutation) = work { owner ->
        val facts = state.facts ?: return@work
        val result = container.gitWorkspace.preview(owner, facts, change); verify(owner)
        state = state.copy(preview = (result as? ApiResult.Success)?.value, problem = problem(result))
        if (result !is ApiResult.Success) load(owner)
    }
    fun dismissPreview() { state = state.copy(preview = null) }
    fun confirm() = work { owner ->
        val preview = state.preview ?: return@work; state = state.copy(preview = null)
        val result = container.gitWorkspace.change(owner, preview); verify(owner)
        load(owner)
        state = state.copy(problem = when {
            result is ApiResult.Success && !result.value.success -> if (result.value.requiresCredentials) "git.workspace.credentials" else "git.workspace.rejected"
            else -> problem(result) ?: state.problem
        }, saved = result is ApiResult.Success && result.value.success)
        if (result is ApiResult.Success && result.value.conflicts.isNotEmpty()) state = state.copy(problem = "git.workspace.conflicts")
        if (result is ApiResult.Success && result.value.success) {
            if (preview.change.action == GitAction.Commit) commitMessage = ""
            if (preview.change.action == GitAction.Resolve && state.facts?.conflicts?.paths?.contains(preview.change.conflict?.path) == false) closeConflict()
        }
        if (state.facts == null) state = state.copy(problem = "git.workspace.unverified", saved = false)
    }
    fun accept(marker: PendingGitMutation) = work { owner ->
        val result = container.gitWorkspace.accept(owner, marker); verify(owner)
        if (result is ApiResult.Success) state = state.copy(selectedId = marker.repositoryId, facts = result.value, pending = container.gitWorkspace.pending(owner),
            diff = null, detail = null, commits = emptyList(), historyLoaded = false, preview = null)
        else state = state.copy(problem = problem(result))
    }
    fun showDiff(path: String, staged: Boolean, reference: String? = null) = work { owner ->
        val id = state.selectedId ?: return@work
        state = state.copy(diff = null)
        val result = container.gitWorkspace.diff(owner, id, path, staged, reference); verify(owner)
        state = state.copy(diff = (result as? ApiResult.Success)?.value, problem = problem(result))
    }
    fun history(skip: Int, search: String) = work { owner ->
        val id = state.selectedId ?: return@work
        state = state.copy(commits = emptyList(), detail = null, historyLoaded = false)
        val result = container.gitWorkspace.log(owner, id, skip, search); verify(owner)
        state = state.copy(commits = (result as? ApiResult.Success)?.value.orEmpty(), skip = skip, search = search,
            historyLoaded = result is ApiResult.Success, problem = problem(result))
    }
    fun detail(sha: String) = work { owner ->
        val id = state.selectedId ?: return@work; state = state.copy(detail = null)
        val result = container.gitWorkspace.detail(owner, id, sha); verify(owner)
        state = state.copy(detail = (result as? ApiResult.Success)?.value, problem = problem(result))
    }
    fun conflict(path: String) = work { owner ->
        val id = state.selectedId ?: return@work
        val old = state.conflict
        val result = container.gitWorkspace.conflict(owner, id, path); verify(owner)
        if (result is ApiResult.Success) {
            if (old?.path != path) conflictDraft = TextFieldValue(result.value.result.orEmpty())
            state = state.copy(conflict = result.value, problem = null)
        } else state = state.copy(problem = problem(result))
    }
    fun editConflict(value: TextFieldValue) { if (!state.busy) conflictDraft = value }
    fun closeConflict() { conflictDraft = TextFieldValue(); state = state.copy(conflict = null, preview = null) }
    fun install() = work { owner ->
        require(owner.privilegedOperations && owner.serverPlatform.equals("linux", true) && state.pending.isEmpty())
        require(state.pendingInstallation || state.engine?.let { !it.available && it.canInstall && it.problemCode == "not_installed" } == true)
        if (installIntent == null) installIntent = container.installations.prepare(owner, InstallationKind.Install, GitInstallationRequest(true))
        val result = container.gitWorkspace.mutations.withLock {
            require(container.gitWorkspace.pending(owner).isEmpty())
            container.installations.submit(requireNotNull(installIntent), container.elevationAnswers)
        }; verify(owner); installationResult(owner, result)
        if (result is ApiResult.Success || result is ApiResult.Problem && !state.pendingInstallation) installIntent = null
    }
    fun pollInstall() = work { owner -> state.installation?.operationId?.let { installationResult(owner, container.installations.operation(owner, it)) } }
    fun cancelInstall() = work { owner ->
        val operation = state.installation ?: return@work
        require(state.installationVerified && operation.state.active && operation.cancellable)
        installationResult(owner, container.installations.cancel(owner, operation.operationId))
    }
    fun recoverInstall(id: String, identified: Boolean) = work { owner ->
        val pending = container.installations.pending(owner).firstOrNull { it.service == InstallationService.Git }
        if (pending != null && !identified) return@work
        installationResult(owner, if (pending == null) container.installations.recoverById(owner, id.trim()) else container.installations.identifyOriginal(owner, pending, id.trim()))
        if (!state.pendingInstallation) installIntent = null
    }
    private suspend fun loadInstallation(owner: SessionState.Active) {
        if (!owner.privilegedOperations) return
        val known = container.installations.pending(owner).firstOrNull { it.service == InstallationService.Git && it.operationId != null }
        if (known != null) {
            val result = container.installations.recover(owner, known); verify(owner)
            if (result is ApiResult.Success && result.value != null) installationResult(owner, ApiResult.Success(result.value))
            else state = state.copy(installationVerified = false)
        } else {
            val active = container.installations.active(owner, InstallationService.Git); verify(owner)
            if (active is ApiResult.Success && active.value != null) installationResult(owner, ApiResult.Success(active.value))
            else if (active is ApiResult.Success) {
                val id = state.installation?.operationId ?: container.operationIndex.forOwner(owner).firstOrNull { it.domain == OperationDomain.Installation && it.resourceId == InstallationService.Git.name }?.operationId
                if (id != null) installationResult(owner, container.installations.operation(owner, id))
            } else state = state.copy(installationVerified = false, problem = problem(active))
        }
    }
    private suspend fun installationResult(owner: SessionState.Active, result: ApiResult<InstallationOperation>) {
        verify(owner)
        state = state.copy(pendingInstallation = container.installations.pending(owner).any { it.service == InstallationService.Git })
        if (result is ApiResult.Success && result.value.service == InstallationService.Git && result.value.kind == InstallationKind.Install) {
            state = state.copy(installation = result.value, installationVerified = true, problem = result.value.problemCode)
            if (!result.value.state.active) {
                val engine = container.gitWorkspace.engine(owner); verify(owner)
                state = state.copy(engine = (engine as? ApiResult.Success)?.value)
            }
        } else state = state.copy(installationVerified = false, problem = problem(result) ?: "git.workspace.unverified")
    }
    private suspend fun load(owner: SessionState.Active) {
        val id = state.selectedId
        val result = if (id != null) container.gitWorkspace.facts(owner, id) else null; verify(owner)
        state = state.copy(facts = (result as? ApiResult.Success)?.value, problem = result?.let(::problem), pending = container.gitWorkspace.pending(owner),
            pendingInstallation = container.installations.pending(owner).any { it.service == InstallationService.Git })
    }
    private fun problem(result: ApiResult<*>): String? = when (result) { is ApiResult.Success -> null; is ApiResult.Problem -> result.code; is ApiResult.Transport -> "git.workspace.unverified" }
    private fun work(block: suspend (SessionState.Active) -> Unit) {
        val owner = state.owner ?: return
        if (state.busy || ServerCapabilities.GIT !in owner.capabilities) return
        val epoch = generation; state = state.copy(busy = true, problem = null, saved = false)
        job = viewModelScope.launch {
            try { block(owner) }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (current(owner, epoch)) state = state.copy(facts = null, preview = null, problem = "git.workspace.unverified") }
            finally { if (current(owner, epoch)) state = state.copy(busy = false,
                pending = runCatching { container.gitWorkspace.pending(owner) }.getOrDefault(state.pending),
                pendingInstallation = runCatching { container.installations.pending(owner).any { it.service == InstallationService.Git } }.getOrDefault(state.pendingInstallation)) }
        }
    }
    private fun current(owner: SessionState.Active, epoch: Int) = state.owner === owner && container.session.state.value === owner && generation == epoch
    private suspend fun verify(owner: SessionState.Active) { currentCoroutineContext().ensureActive(); if (state.owner !== owner || container.session.state.value !== owner) throw CancellationException("Git workspace changed") }
}
