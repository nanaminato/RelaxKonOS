package app.relaxkonos.mobile.ui.manage.git

import androidx.compose.ui.text.input.TextFieldValue
import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Before
import org.junit.After
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class GitWorkspaceViewModelTest {
    private class Storage : InstallationRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private class IndexStorage : OperationIndexStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val base = FakeGateway()
    private val id = "11111111-1111-1111-1111-111111111111"
    private val otherId = "22222222-2222-2222-2222-222222222222"
    private val sha = "a".repeat(40)
    private var repositories = listOf(GitRepository(id, "Original", "/repo", "main"))
    private var status = GitStatus("main", listOf(GitChange("a.txt", "modified")), emptyList(), emptyList(), emptyList(), 0, 0, "origin/main", false, "e".repeat(64))
    private var missing = false
    private val reads = mutableListOf<String>()
    private var sends = 0
    private val conflict = GitConflictFile("a.txt", "b".repeat(64), "base", "ours", "theirs", "result", true)
    private var onConflict: suspend () -> ApiResult<GitConflictFile> = { ApiResult.Success(conflict) }
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun gitEngine(serverUrl: String, accessToken: String) = ApiResult.Success(GitEngine(true, "", "2", false))
        override suspend fun gitRepositories(serverUrl: String, accessToken: String) = ApiResult.Success(repositories)
        override suspend fun gitStatus(serverUrl: String, accessToken: String, id: String): ApiResult<GitStatus> {
            reads += id
            return if (missing && id == this@GitWorkspaceViewModelTest.id) ApiResult.Problem(404, "git.repository.not_found", null) else ApiResult.Success(status)
        }
        override suspend fun gitBranches(serverUrl: String, accessToken: String, id: String) = ApiResult.Success(listOf(GitBranch("main", sha, true, false, "origin/main", 0, 0)))
        override suspend fun gitConflicts(serverUrl: String, accessToken: String, id: String) = ApiResult.Success(GitConflictState("merge", listOf("a.txt")))
        override suspend fun gitLog(serverUrl: String, accessToken: String, id: String, skip: Int, search: String) =
            ApiResult.Success(listOf(GitCommit(sha, sha.take(7), "author", "date", "subject")))
        override suspend fun gitConflict(serverUrl: String, accessToken: String, id: String, path: String) = onConflict()
        override suspend fun gitMutation(serverUrl: String, accessToken: String, id: String, change: GitMutation): ApiResult<GitOperation> { sends++; return ApiResult.Transport(null) }
    }
    private val session = AuthSession(gateway)
    private val index = OperationIndex(IndexStorage())
    private val journal = GitWorkspaceJournal(Storage())
    private val workspace = GitWorkspaceRepository(gateway, session, journal) { ApiResult.Success(Unit) }
    private val installations = InstallationRepository(gateway, session,
        ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage())),
        index, InstallationRequestJournal(Storage()))
    private val store = ViewModelStore()
    private lateinit var model: GitWorkspaceViewModel
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = GitWorkspaceViewModel(session, GitRepositoryClient(gateway, session, index), workspace, installations, index, ElevationAnswerProvider.Declines)
        store.put("git", model)
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login() {
        base.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.GIT), privilegedOperations = false),
                executionEligibility = it.executionEligibility.copy(available = true))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://host.test"), "alice", "pw".toCharArray()) {}
    }
    @Test fun `preview refusal remains visible after successful fact refresh`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        assertNotNull(model.state.facts)
        status = status.copy(configVersion = "f".repeat(64))
        model.prepare(GitMutation(GitAction.Resolve, conflict = conflict, choice = "ours")); advanceUntilIdle()
        assertNull(model.state.preview); assertNotNull(model.state.facts)
        assertEquals("git.workspace.facts_changed", model.state.problem)
        assertFalse(model.state.busy); assertEquals(0, sends)
    }
    @Test fun `failed conflict reload preserves the open file and resolution draft`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.conflict("a.txt"); advanceUntilIdle(); model.editConflict(TextFieldValue("local resolution"))
        onConflict = { ApiResult.Transport(null) }
        model.conflict("a.txt"); advanceUntilIdle()
        assertEquals(conflict, model.state.conflict); assertEquals("local resolution", model.conflictDraft.text)
        assertEquals("git.workspace.unverified", model.state.problem); assertFalse(model.state.busy)
        val firstFeedback = model.state.feedbackVersion
        onConflict = { throw IllegalStateException("private detail") }
        model.conflict("a.txt"); advanceUntilIdle()
        assertEquals(conflict, model.state.conflict); assertEquals("local resolution", model.conflictDraft.text)
        assertNull(model.state.facts); assertFalse(model.state.busy); assertEquals(0, sends)
        assertEquals("git.workspace.unverified", model.state.problem); assertTrue(model.state.feedbackVersion > firstFeedback)
    }
    @Test fun `late conflict response cannot restore an old identity editor`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        val entered = CompletableDeferred<Unit>(); val release = CompletableDeferred<Unit>()
        onConflict = {
            entered.complete(Unit)
            withContext(NonCancellable) { release.await() }
            ApiResult.Success(conflict)
        }
        model.conflict("a.txt"); runCurrent(); entered.await()
        session.clearSession(); runCurrent()
        login(); runCurrent(); model.commitMessage = "new identity message"
        release.complete(Unit); advanceUntilIdle()
        assertNull(model.state.conflict); assertEquals("", model.conflictDraft.text)
        assertEquals("new identity message", model.commitMessage)
        assertFalse(model.state.busy); assertNull(model.state.problem); assertEquals(0, sends)
    }
    @Test fun `conflict reload restores unavailable facts without replacing the resolution draft`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.conflict("a.txt"); advanceUntilIdle(); model.editConflict(TextFieldValue("local resolution"))
        onConflict = { throw IllegalStateException("private detail") }
        model.conflict("a.txt"); advanceUntilIdle()
        assertNull(model.state.facts)
        val latest = conflict.copy(revision = "c".repeat(64), theirs = "new theirs")
        onConflict = { ApiResult.Success(latest) }
        model.conflict("a.txt"); advanceUntilIdle()
        assertNotNull(model.state.facts); assertEquals(id, model.state.facts?.repositoryId)
        assertEquals(latest, model.state.conflict); assertEquals("local resolution", model.conflictDraft.text)
        assertNull(model.state.problem); assertFalse(model.state.busy); assertEquals(0, sends)
    }
    @Test fun `conflict recovery cannot clear a missing original repository failure`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.conflict("a.txt"); advanceUntilIdle(); model.editConflict(TextFieldValue("local resolution"))
        onConflict = { throw IllegalStateException("private detail") }
        model.conflict("a.txt"); advanceUntilIdle()
        var conflictReads = 0
        onConflict = { conflictReads++; ApiResult.Success(conflict) }
        missing = true; reads.clear()
        model.conflict("a.txt"); advanceUntilIdle()
        assertNull(model.state.facts); assertEquals(id, model.state.selectedId)
        assertEquals(listOf(id), reads); assertEquals(0, conflictReads)
        assertEquals("git.repository.not_found", model.state.problem)
        assertEquals(conflict, model.state.conflict); assertEquals("local resolution", model.conflictDraft.text)
        assertFalse(model.state.busy); assertEquals(0, sends)
    }
    @Test fun `missing repository cannot retarget an open conflict draft`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.conflict("a.txt"); advanceUntilIdle(); model.editConflict(TextFieldValue("local resolution"))
        repositories = listOf(GitRepository(otherId, "Other", "/other", "main")); missing = true
        reads.clear(); model.refresh(); advanceUntilIdle()
        assertEquals(id, model.state.selectedId); assertEquals(listOf(id), reads)
        assertNull(model.state.facts); assertEquals(conflict, model.state.conflict)
        assertEquals("local resolution", model.conflictDraft.text); assertFalse(model.state.busy)
        assertEquals(0, sends)
    }
    @Test fun `unknown resolution adoption preserves draft and failed verification keeps the marker`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.conflict("a.txt"); advanceUntilIdle(); model.editConflict(TextFieldValue("local resolution"))
        model.prepare(GitMutation(GitAction.Resolve, conflict = conflict, choice = "edited", content = "local resolution")); advanceUntilIdle()
        assertNotNull(model.state.preview)
        model.confirm(); advanceUntilIdle()
        val marker = model.state.pending.single()
        assertEquals(1, sends); assertEquals(conflict, model.state.conflict)
        missing = true
        model.accept(marker); advanceUntilIdle()
        assertEquals(listOf(marker), model.state.pending); assertEquals("local resolution", model.conflictDraft.text)
        assertEquals("git.repository.not_found", model.state.problem); assertFalse(model.state.busy)
        missing = false
        model.accept(marker); advanceUntilIdle()
        assertTrue(model.state.pending.isEmpty()); assertEquals(id, model.state.selectedId)
        assertEquals(conflict, model.state.conflict); assertEquals("local resolution", model.conflictDraft.text)
        assertFalse(model.state.busy); assertEquals(1, sends)
    }
    @Test fun `adoption from another repository cannot retarget an open resolution draft`() = runTest {
        login(); runCurrent(); model.refresh(); advanceUntilIdle()
        model.conflict("a.txt"); advanceUntilIdle(); model.editConflict(TextFieldValue("local resolution"))
        val owner = requireNotNull(model.state.owner)
        val marker = journal.begin(owner, otherId, GitAction.Resolve)
        model.accept(marker); advanceUntilIdle()
        assertEquals(id, model.state.selectedId); assertEquals(id, model.state.facts?.repositoryId)
        assertEquals(conflict, model.state.conflict); assertEquals("local resolution", model.conflictDraft.text)
        assertEquals(listOf(marker), journal.pending(owner))
        assertEquals("git.workspace.facts_changed", model.state.problem); assertFalse(model.state.busy)
        assertEquals(0, sends)
    }
}
