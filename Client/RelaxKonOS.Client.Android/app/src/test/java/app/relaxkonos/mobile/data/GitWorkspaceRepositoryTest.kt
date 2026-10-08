package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class GitWorkspaceRepositoryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes?.copyOf()
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val storage = Storage()
    private val base = FakeGateway()
    private val id = "11111111-1111-1111-1111-111111111111"
    private val sha = "a".repeat(40)
    private var status = GitStatus("main", listOf(GitChange("a.txt", "modified")), emptyList(), emptyList(), emptyList(), 0, 0, "origin/main", false, "e".repeat(64))
    private var branches = listOf(GitBranch("main", sha, true, false, "origin/main", 0, 0))
    private var conflicts = GitConflictState(null, emptyList())
    private var conflict = GitConflictFile("a.txt", "b".repeat(64), "base", "ours", "theirs", "result", true)
    private var diff = GitDiff("a.txt", "c".repeat(64), "patch", 1, 0, false, false)
    private var sends = 0
    private var available = true
    private var readsFail = false
    private var onSend: suspend (GitMutation) -> ApiResult<GitOperation> = { ApiResult.Transport(null) }
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun gitStatus(serverUrl: String, accessToken: String, id: String): ApiResult<GitStatus> = if (readsFail) ApiResult.Transport(null) else ApiResult.Success(status)
        override suspend fun gitBranches(serverUrl: String, accessToken: String, id: String) = ApiResult.Success(branches)
        override suspend fun gitConflicts(serverUrl: String, accessToken: String, id: String) = ApiResult.Success(conflicts)
        override suspend fun gitConflict(serverUrl: String, accessToken: String, id: String, path: String) = ApiResult.Success(conflict)
        override suspend fun gitLog(serverUrl: String, accessToken: String, id: String, skip: Int, search: String) = ApiResult.Success(listOf(GitCommit(sha, sha.take(7), "author", "date", "subject")))
        override suspend fun gitDiff(serverUrl: String, accessToken: String, id: String, path: String, staged: Boolean, reference: String?) = ApiResult.Success(diff)
        override suspend fun gitMutation(serverUrl: String, accessToken: String, id: String, change: GitMutation): ApiResult<GitOperation> { sends++; return onSend(change) }
    }
    private val session = AuthSession(gateway)
    private val journal = GitWorkspaceJournal(storage)
    private val client = GitWorkspaceRepository(gateway, session, journal) {
        if (available) ApiResult.Success(Unit) else ApiResult.Problem(409, "installation", null)
    }
    private suspend fun signIn(eligible: Boolean = true): SessionState.Active {
        base.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.GIT)),
            executionEligibility = it.executionEligibility.copy(available = eligible)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private suspend fun preview(owner: SessionState.Active, change: GitMutation = GitMutation(GitAction.Commit, message = "subject")): GitMutationPreview {
        val facts = (client.facts(owner, id) as ApiResult.Success).value
        return (client.preview(owner, facts, change) as ApiResult.Success).value
    }
    @Test fun `transport loss persists an account bound marker and prohibits replay`() = runTest {
        val owner = signIn(); val preview = preview(owner)
        assertTrue(client.change(owner, preview) is ApiResult.Transport); assertEquals(1, sends)
        assertTrue(client.change(owner, preview) is ApiResult.Problem); assertEquals(1, sends)
        val persisted = GitWorkspaceJournal(storage).pending(owner).single()
        assertEquals(GitAction.Commit, persisted.action)
        assertFalse(storage.bytes!!.decodeToString().contains("subject"))
        readsFail = true; assertTrue(client.accept(owner, persisted) is ApiResult.Transport); assertEquals(1, journal.pending(owner).size)
        readsFail = false; assertTrue(client.accept(owner, persisted) is ApiResult.Success); assertTrue(journal.pending(owner).isEmpty()); assertEquals(1, sends)
    }
    @Test fun `ineligible ordinary identity cannot prepare or dispatch mutations`() = runTest {
        val owner = signIn(false); val facts = (client.facts(owner, id) as ApiResult.Success).value
        val change = GitMutation(GitAction.Push)
        assertTrue(client.preview(owner, facts, change) is ApiResult.Problem)
        assertTrue(client.change(owner, GitMutationPreview(facts, change, emptyList())) is ApiResult.Problem)
        assertEquals(0, sends); assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `binary or truncated diff changes stop a confirmation even when displayed patch is unchanged`() = runTest {
        val owner = signIn(); diff = diff.copy(patch = "", binary = true, truncated = true)
        val preview = preview(owner); diff = diff.copy(version = "d".repeat(64))
        assertTrue(client.change(owner, preview) is ApiResult.Problem); assertEquals(0, sends); assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `a target branch advancing or an installation starting invalidates confirmation`() = runTest {
        val owner = signIn(); val preview = preview(owner)
        branches = branches.map { it.copy(sha = "f".repeat(40)) }
        assertTrue(client.change(owner, preview) is ApiResult.Problem); assertEquals(0, sends)
        val next = preview(owner); available = false
        assertTrue(client.change(owner, next) is ApiResult.Problem); assertEquals(0, sends)
    }
    @Test fun `changing remote destinations without changing upstream names invalidates a push confirmation`() = runTest {
        val owner = signIn(); val preview = preview(owner, GitMutation(GitAction.Push))
        status = status.copy(configVersion = "f".repeat(64))
        assertTrue(client.change(owner, preview) is ApiResult.Problem); assertEquals(0, sends)
    }
    @Test fun `explicit unauthorized retry rechecks repository facts before dispatch`() = runTest {
        val owner = signIn(); val preview = preview(owner)
        base.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("new", "refresh", null, null)) }
        onSend = { status = status.copy(ahead = 1); ApiResult.Problem(401, "unauthorized", null) }
        val result = client.change(owner, preview)
        assertEquals("git.workspace.facts_changed", (result as ApiResult.Problem).code)
        assertEquals(1, sends); assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `known conflict receipt clears the marker only after readable facts`() = runTest {
        val owner = signIn(); val preview = preview(owner, GitMutation(GitAction.Pull))
        onSend = { conflicts = GitConflictState("merge", listOf("a.txt")); ApiResult.Success(GitOperation(false, "pull", false, listOf("a.txt"))) }
        assertTrue(client.change(owner, preview) is ApiResult.Success); assertTrue(journal.pending(owner).isEmpty())
        assertEquals("merge", (client.facts(owner, id) as ApiResult.Success).value.conflicts.operation)
    }
    @Test fun `missing readback and mismatched mutation receipt remain pending`() = runTest {
        val owner = signIn(); val preview = preview(owner)
        onSend = { readsFail = true; ApiResult.Success(GitOperation(true, "commit", false, emptyList())) }
        assertTrue(client.change(owner, preview) is ApiResult.Transport); assertEquals(1, journal.pending(owner).size)
        readsFail = false; client.accept(owner, journal.pending(owner).single())
        onSend = { ApiResult.Success(GitOperation(true, "push", false, emptyList())) }
        assertTrue(client.change(owner, preview(owner)) is ApiResult.Transport); assertEquals(1, journal.pending(owner).size)
    }
    @Test fun `conflict reads reject another path and malformed revision without dispatching mutations`() = runTest {
        val owner = signIn()
        conflict = conflict.copy(path = "other.txt")
        assertTrue(client.conflict(owner, id, "a.txt") is ApiResult.Transport)
        conflict = conflict.copy(path = "a.txt", revision = "invalid")
        assertTrue(client.conflict(owner, id, "a.txt") is ApiResult.Transport)
        conflict = conflict.copy(revision = "B".repeat(64))
        assertEquals(conflict, (client.conflict(owner, id, "a.txt") as ApiResult.Success).value)
        assertEquals(0, sends)
    }
    @Test fun `conflict revision change and normal writes during merge cannot dispatch`() = runTest {
        val owner = signIn(); conflicts = GitConflictState("merge", listOf("a.txt"))
        val preview = preview(owner, GitMutation(GitAction.Resolve, conflict = conflict, choice = "edited", content = "resolved"))
        conflict = conflict.copy(revision = "d".repeat(64))
        assertTrue(client.change(owner, preview) is ApiResult.Problem); assertEquals(0, sends)
        val facts = (client.facts(owner, id) as ApiResult.Success).value
        assertTrue(runCatching { client.preview(owner, facts, GitMutation(GitAction.Commit, message = "x")) }.isFailure)
        assertTrue(runCatching { client.preview(owner, facts, GitMutation(GitAction.Continue, operation = "merge")) }.isFailure)
    }
    @Test fun `old session and late mutation response retain the original owner marker`() = runTest {
        val owner = signIn(); val preview = preview(owner); val entered = CompletableDeferred<Unit>(); val release = CompletableDeferred<Unit>()
        onSend = { entered.complete(Unit); release.await(); ApiResult.Success(GitOperation(true, "commit", false, emptyList())) }
        val task = async { runCatching { client.change(owner, preview) } }
        entered.await(); signIn(); release.complete(Unit)
        assertTrue(task.await().exceptionOrNull() is CancellationException); assertEquals(1, journal.pending(owner).size)
        assertTrue(runCatching { client.change(owner, preview) }.exceptionOrNull() is CancellationException); assertEquals(1, sends)
    }
    @Test fun `only staged content may be committed and renamed paths can be explicitly unstaged`() = runTest {
        val owner = signIn()
        assertTrue(runCatching { preview(owner, GitMutation(GitAction.Commit, paths = listOf("a.txt"), message = "x")) }.isFailure)
        status = status.copy(staged = emptyList()); assertTrue(runCatching { preview(owner) }.isFailure)
        status = status.copy(staged = listOf(GitChange("a.txt", "renamed", "old.txt")))
        assertEquals(listOf("a.txt"), preview(owner).diffs.map { it.path })
        assertTrue(runCatching { preview(owner, GitMutation(GitAction.Stage, paths = listOf("outside.txt"))) }.isFailure)
    }
}
