package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

data class GitWorkspaceFacts(val repositoryId: String, val status: GitStatus, val branches: List<GitBranch>, val conflicts: GitConflictState, val head: String?)
data class GitMutationPreview(val facts: GitWorkspaceFacts, val change: GitMutation, val diffs: List<GitDiff>)
class GitWorkspaceRepository(private val gateway: RelaxKonGateway, private val session: AuthSession,
    private val journal: GitWorkspaceJournal, private val installationGate: suspend (SessionState.Active) -> ApiResult<Unit>) {
    val mutations = Mutex()
    fun pending(owner: SessionState.Active): List<PendingGitMutation> { verify(owner); return journal.pending(owner) }
    suspend fun engine(owner: SessionState.Active) = call(owner) { u, t -> gateway.gitEngine(u, t) }
    suspend fun facts(owner: SessionState.Active, id: String): ApiResult<GitWorkspaceFacts> {
        InstallationRoutes.canonicalId(id)
        val status = call(owner) { u, t -> gateway.gitStatus(u, t, id) }; if (status !is ApiResult.Success) return failure(status)
        val branches = call(owner) { u, t -> gateway.gitBranches(u, t, id) }; if (branches !is ApiResult.Success) return failure(branches)
        val conflicts = call(owner) { u, t -> gateway.gitConflicts(u, t, id) }; if (conflicts !is ApiResult.Success) return failure(conflicts)
        val head = log(owner, id, 0, ""); if (head !is ApiResult.Success) return failure(head)
        return ApiResult.Success(GitWorkspaceFacts(id, status.value, branches.value, conflicts.value, head.value.firstOrNull()?.sha))
    }
    suspend fun diff(owner: SessionState.Active, id: String, path: String, staged: Boolean, reference: String? = null): ApiResult<GitDiff> {
        require(GitWorkspacePolicy.path(path))
        InstallationRoutes.canonicalId(id); require(reference == null || reference.matches(Regex("[0-9a-f]{40}|[0-9a-f]{64}")))
        return call(owner) { u, t -> gateway.gitDiff(u, t, id, path, staged, reference) }.let { result ->
            if (result is ApiResult.Success && (result.value.path != path || !result.value.version.matches(Regex("[0-9a-f]{64}")))) ApiResult.Transport("Invalid Git diff receipt") else result
        }
    }
    suspend fun log(owner: SessionState.Active, id: String, skip: Int, search: String): ApiResult<List<GitCommit>> {
        InstallationRoutes.canonicalId(id); require(skip >= 0 && search.length <= 4096)
        return call(owner) { u, t -> gateway.gitLog(u, t, id, skip, search) }.let { result ->
            if (result is ApiResult.Success && (result.value.size > 50 || result.value.any { !it.sha.matches(Regex("[0-9a-f]{40}|[0-9a-f]{64}")) })) ApiResult.Transport("Invalid Git history") else result
        }
    }
    suspend fun detail(owner: SessionState.Active, id: String, sha: String): ApiResult<GitCommitDetail> {
        InstallationRoutes.canonicalId(id); require(sha.matches(Regex("[0-9a-f]{40}|[0-9a-f]{64}")))
        return call(owner) { u, t -> gateway.gitCommitDetail(u, t, id, sha) }.let {
        if (it is ApiResult.Success && it.value.sha != sha) ApiResult.Transport("Invalid Git commit identity") else it
        }
    }
    suspend fun conflict(owner: SessionState.Active, id: String, path: String): ApiResult<GitConflictFile> {
        InstallationRoutes.canonicalId(id); require(GitWorkspacePolicy.path(path))
        return call(owner) { u, t -> gateway.gitConflict(u, t, id, path) }.let {
        if (it is ApiResult.Success && (it.value.path != path || !it.value.revision.matches(Regex("[0-9A-Fa-f]{64}")))) ApiResult.Transport("Invalid conflict identity") else it
        }
    }
    suspend fun preview(owner: SessionState.Active, expected: GitWorkspaceFacts, change: GitMutation): ApiResult<GitMutationPreview> = mutations.withLock {
        verify(owner)
        if (!owner.executionEligibility.available || ServerCapabilities.GIT !in owner.capabilities) return@withLock ApiResult.Problem(403, "git.workspace.identity_unavailable", null)
        GitWorkspacePolicy.validate(change)
        val current = facts(owner, expected.repositoryId); if (current !is ApiResult.Success) return@withLock failure(current)
        if (current.value != expected) return@withLock changed()
        val status = expected.status
        when (change.action) {
            GitAction.DeleteBranch -> require(expected.branches.any { it.name == change.branch && !it.current && !it.remote })
            GitAction.Checkout -> require(expected.branches.any { it.name == change.branch })
            GitAction.CreateBranch -> require(expected.branches.none { it.name == change.branch })
            GitAction.Commit -> require(status.staged.isNotEmpty() && expected.conflicts.operation == null && expected.conflicts.paths.isEmpty())
            GitAction.Stage -> require(change.paths.all { path -> (status.unstaged + status.untracked).any { it.path == path || it.oldPath == path } })
            GitAction.Unstage -> require(change.paths.all { path -> status.staged.any { it.path == path || it.oldPath == path } })
            else -> Unit
        }
        if (change.action == GitAction.Continue) require(expected.conflicts.paths.isEmpty() && expected.conflicts.operation == change.operation)
        if (change.action == GitAction.Abort) require(expected.conflicts.operation == change.operation)
        if (change.action !in listOf(GitAction.Resolve, GitAction.Continue, GitAction.Abort)) require(expected.conflicts.paths.isEmpty() && expected.conflicts.operation == null)
        if (change.action == GitAction.Resolve) {
            require(change.conflict?.path in expected.conflicts.paths)
            val latest = conflict(owner, expected.repositoryId, requireNotNull(change.conflict).path)
            if (latest !is ApiResult.Success) return@withLock failure(latest)
            if (latest.value != change.conflict) return@withLock changed()
        }
        val paths = if (change.action == GitAction.Commit) status.staged.map { it.path } else change.paths
        val diffs = mutableListOf<GitDiff>()
        for (path in paths) {
            val result = diff(owner, expected.repositoryId, path, change.action in listOf(GitAction.Commit, GitAction.Unstage))
            if (result !is ApiResult.Success) return@withLock failure(result)
            diffs += result.value
        }
        ApiResult.Success(GitMutationPreview(expected, change, diffs))
    }
    suspend fun change(owner: SessionState.Active, preview: GitMutationPreview): ApiResult<GitOperation> = mutations.withLock {
        verify(owner); val id = preview.facts.repositoryId; GitWorkspacePolicy.validate(preview.change)
        if (!owner.executionEligibility.available || ServerCapabilities.GIT !in owner.capabilities) return@withLock ApiResult.Problem(403, "git.workspace.identity_unavailable", null)
        if (pending(owner).any { it.repositoryId == id }) return@withLock ApiResult.Problem(409, "git.workspace.pending", null)
        val gate = installationGate(owner); if (gate !is ApiResult.Success) return@withLock failure(gate)
        suspend fun unchanged(): Boolean {
            if (installationGate(owner) !is ApiResult.Success) return false
            val current = facts(owner, id); if (current !is ApiResult.Success || current.value != preview.facts) return false
            for (previous in preview.diffs) {
                val now = diff(owner, id, previous.path, preview.change.action in listOf(GitAction.Commit, GitAction.Unstage))
                if (now !is ApiResult.Success || now.value.version != previous.version) return false
            }
            preview.change.conflict?.let { if (conflict(owner, id, it.path) != ApiResult.Success(it)) return false }
            return true
        }
        if (!unchanged()) return@withLock changed()
        val marker = journal.begin(owner, id, preview.change.action)
        val result = call(owner) { u, t ->
            // AuthSession may retry only an explicit 401. Recheck facts before that authorized retry.
            if (!unchanged()) changed<GitOperation>() else gateway.gitMutation(u, t, id, preview.change)
        }
        val expectedOperation = if (preview.change.action in listOf(GitAction.Continue, GitAction.Abort)) preview.change.operation else preview.change.action.operation
        if (result is ApiResult.Success && result.value.operation == expectedOperation) {
            if (facts(owner, id) is ApiResult.Success) journal.complete(marker)
            else return@withLock ApiResult.Transport("Git receipt received but current facts are unavailable")
        } else if (result is ApiResult.Problem && (result.status in setOf(401, 403) || result.code == "git.workspace.facts_changed")) journal.complete(marker)
        return@withLock if (result is ApiResult.Success && result.value.operation != expectedOperation) ApiResult.Transport("Invalid Git mutation receipt") else result
    }
    suspend fun accept(owner: SessionState.Active, marker: PendingGitMutation): ApiResult<GitWorkspaceFacts> = mutations.withLock {
        require(marker in pending(owner))
        val result = facts(owner, marker.repositoryId)
        if (result is ApiResult.Success) journal.complete(marker)
        result
    }
    private suspend fun <T> call(owner: SessionState.Active, action: suspend (String, String) -> ApiResult<T>): ApiResult<T> {
        verify(owner); val result = session.authenticated { u, t -> verify(owner); action(u, t) }; verify(owner); return result
    }
    private fun verify(owner: SessionState.Active) { if (session.state.value !== owner) throw CancellationException("Git workspace session changed") }
    private fun <T> changed(): ApiResult<T> = ApiResult.Problem(409, "git.workspace.facts_changed", null)
    private fun <T> failure(result: ApiResult<*>): ApiResult<T> = when (result) { is ApiResult.Problem -> result; is ApiResult.Transport -> result; else -> error("Not a failure") }
}
