package app.relaxkonos.mobile.ui.manage.scripts

import androidx.lifecycle.ViewModelStore
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Before
import org.junit.After
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ScriptsViewModelTest {
    private val base = FakeGateway()
    private fun task(id: String, state: String = "running") = ScriptTask(id, "/bin/true", "alice", state,
        "date", null, null, null, emptyList(), false)
    private var details: suspend (String) -> ApiResult<ScriptTaskResult> = { ApiResult.Success(ScriptTaskResult(true, "", task(it))) }
    private var submission: suspend () -> ApiResult<ScriptTaskResult> = { ApiResult.Transport(null) }
    private var cancellation: suspend () -> ApiResult<ScriptTaskResult> = { ApiResult.Transport(null) }
    private var reads = 0
    private var submissions = 0
    private var submittedKey = ""
    private var cancellations = 0
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun scriptTasks(serverUrl: String, accessToken: String) = ApiResult.Success(ScriptTasksResult(true, "", listOf(task("a"))))
        override suspend fun scriptTask(serverUrl: String, accessToken: String, id: String): ApiResult<ScriptTaskResult> { reads++; return details(id) }
        override suspend fun scriptSubmit(serverUrl: String, accessToken: String, request: ScriptRequest, key: String): ApiResult<ScriptTaskResult> { submissions++; submittedKey = key; return submission() }
        override suspend fun scriptCancel(serverUrl: String, accessToken: String, id: String): ApiResult<ScriptTaskResult> { cancellations++; return cancellation() }
    }
    private val session = AuthSession(gateway)
    private val index = OperationIndex(object : OperationIndexStorage {
        override fun read(): ByteArray? = null
        override fun write(bytes: ByteArray) = Unit
    })
    private val store = ViewModelStore()
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        var failRead = false
        var failWrite = false
        override fun read(): ByteArray? { check(!failRead); return bytes }
        override fun write(bytes: ByteArray) { check(!failWrite); this.bytes = bytes.copyOf() }
    }
    private val storage = Storage()
    private fun newModel() = ScriptsViewModel(session, ScriptTaskRepository(gateway, session, index), ScriptRequestJournal(storage))
    private lateinit var model: ScriptsViewModel
    @Before fun setup() {
        Dispatchers.setMain(StandardTestDispatcher())
        model = newModel()
        store.put("scripts", model)
    }
    @After fun cleanup() { store.clear(); Dispatchers.resetMain() }
    private suspend fun login(user: String = "alice"): SessionState.Active {
        base.onLogin = { _, _, _ -> ApiResult.Success(loginSession(userName = user)) }
        session.login(ServerConnectionIdentityRules.direct("https://host"), user, "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private fun request(password: CharArray? = null) = ScriptRequest("/bin/true", emptyList(), "/tmp", emptyMap(), 30,
        "alice", password?.let { GuardianApproval("root", it) })

    @Test fun `changing selection discards a slow old response`() = runTest {
        model.load(login()); runCurrent()
        val release = CompletableDeferred<Unit>()
        details = { id ->
            if (id == "a") withContext(NonCancellable) { release.await() }
            ApiResult.Success(ScriptTaskResult(true, "", task(id)))
        }
        model.select("a"); runCurrent()
        model.select("b"); runCurrent()
        assertNull(model.state.value.selected)
        release.complete(Unit); runCurrent()
        assertEquals("b", model.state.value.selected?.id)
        assertFalse(model.state.value.error)
    }

    @Test fun `polling awaits slow reads and stops after read failure`() = runTest {
        model.load(login()); runCurrent(); model.select("a"); runCurrent()
        val release = CompletableDeferred<Unit>()
        details = { release.await(); ApiResult.Transport(null) }
        val poll = launch { model.observeSelected() }
        advanceTimeBy(10_001); runCurrent()
        assertEquals(2, reads)
        release.complete(Unit); runCurrent()
        assertTrue(model.state.value.error)
        advanceTimeBy(10_001); runCurrent()
        assertTrue(poll.isCompleted)
        assertEquals(2, reads)
    }

    @Test fun `mismatched details cannot replace selected task`() = runTest {
        model.load(login()); runCurrent(); model.select("a"); runCurrent()
        details = { ApiResult.Success(ScriptTaskResult(true, "", task("other"))) }
        model.select("a"); runCurrent()
        assertEquals("a", model.state.value.selected?.id)
        assertTrue(model.state.value.error)
    }

    @Test fun `cancel remains busy during readback and does not repeat writes`() = runTest {
        model.load(login()); runCurrent(); model.select("a"); runCurrent()
        cancellation = { ApiResult.Success(ScriptTaskResult(true, "", task("a", "cancelling"))) }
        val release = CompletableDeferred<Unit>()
        details = { release.await(); ApiResult.Success(ScriptTaskResult(true, "", task("a", "cancelled"))) }
        model.cancel("a"); runCurrent()
        assertTrue(model.state.value.loading)
        model.cancel("a"); model.submit(request()); runCurrent()
        assertEquals(1, cancellations); assertEquals(0, submissions)
        release.complete(Unit); runCurrent()
        assertFalse(model.state.value.loading)
        assertEquals("cancelled", model.state.value.selected?.state)
    }

    @Test fun `failed submission stays visible and duplicate approval is erased`() = runTest {
        model.load(login()); runCurrent()
        model.openEditor()
        submission = { ApiResult.Success(ScriptTaskResult(false, "guardian.script_invalid", null)) }
        val first = "first".toCharArray(); val duplicate = "second".toCharArray()
        model.submit(request(first)); model.submit(request(duplicate)); runCurrent()
        assertEquals(1, submissions)
        assertTrue(first.all { it == '\u0000' }); assertTrue(duplicate.all { it == '\u0000' })
        assertTrue(model.state.value.error)
        assertEquals("guardian.script_invalid", model.state.value.problemCode)
    }

    @Test fun `account switch clears task and late old read cannot restore it`() = runTest {
        model.load(login()); runCurrent(); model.select("a"); runCurrent()
        val release = CompletableDeferred<Unit>()
        details = { withContext(NonCancellable) { release.await() }; ApiResult.Success(ScriptTaskResult(true, "", task(it))) }
        model.select("a"); runCurrent()
        val next = login("bob"); runCurrent()
        assertNull(model.state.value.selected)
        model.load(next); runCurrent(); release.complete(Unit); runCurrent()
        assertNull(model.state.value.selected)
        assertFalse(model.state.value.loading)
    }

    @Test fun `loading a new session for the same account rejects its old task response`() = runTest {
        model.load(login()); runCurrent(); model.select("a"); runCurrent()
        val release = CompletableDeferred<Unit>()
        details = { withContext(NonCancellable) { release.await() }; ApiResult.Success(ScriptTaskResult(true, "", task(it))) }
        model.select("a"); runCurrent()
        model.load(login()); runCurrent()
        assertNull(model.state.value.selected)
        release.complete(Unit); runCurrent()
        assertNull(model.state.value.selected)
        assertFalse(model.state.value.loading)
    }

    @Test fun `clearing before submission starts erases approval without sending`() = runTest {
        model.load(login()); runCurrent()
        model.openEditor()
        val password = "secret".toCharArray()
        model.submit(request(password)); store.clear(); runCurrent()
        assertEquals(0, submissions)
        assertTrue(password.all { it == '\u0000' })
    }

    @Test fun `accepted submission closes draft and sends only once`() = runTest {
        model.load(login()); runCurrent()
        model.openEditor()
        submission = { ApiResult.Success(ScriptTaskResult(true, "", task(submittedKey, "queued"))) }
        model.submit(request()); model.submit(request()); runCurrent()
        assertEquals(1, submissions)
        assertNull(model.state.value.draft)
        assertEquals(submittedKey, model.state.value.selected?.id)
        model.load(session.state.value as SessionState.Active); runCurrent()
        assertNull(model.state.value.draft)
    }

    @Test fun `missing task receipt retains editor outcome and blocks immediate replay`() = runTest {
        model.load(login()); runCurrent()
        model.openEditor()
        submission = { ApiResult.Success(ScriptTaskResult(true, "", null)) }
        model.submit(request()); runCurrent()
        assertNotNull(model.state.value.draft)
        assertTrue(model.state.value.error)
        assertEquals("scripts.write_unknown", model.state.value.problemCode)
        val password = "secret".toCharArray()
        model.submit(request(password)); runCurrent()
        assertEquals(1, submissions)
        assertTrue(password.all { it == '\u0000' })
    }

    @Test fun `transport failure retains failed outcome without accepting or replaying`() = runTest {
        model.load(login()); runCurrent()
        model.openEditor()
        model.submit(request()); runCurrent()
        assertFalse(model.state.value.loading)
        assertNotNull(model.state.value.draft)
        assertEquals("scripts.write_unknown", model.state.value.problemCode)
        model.submit(request()); runCurrent()
        assertEquals(1, submissions)
    }

    @Test fun `reentering the same session preserves full draft and unknown feedback`() = runTest {
        val owner = login(); model.load(owner); runCurrent(); model.openEditor()
        val draft = ScriptDraft("other", "/bin/job", "first\nsecond", "/work", "KEY=value", "42", "admin")
        model.updateDraft(draft)
        model.submit(request()); runCurrent()
        model.load(owner); model.openEditor(); model.updateDraft(draft.copy(executable = "/changed")); runCurrent()
        assertEquals(draft, model.state.value.draft)
        assertEquals("scripts.write_unknown", model.state.value.problemCode)
        assertTrue(model.state.value.error)
        assertEquals(1, submissions)
    }

    @Test fun `busy submission refuses draft changes and close then known failure preserves it`() = runTest {
        model.load(login()); runCurrent(); model.openEditor()
        val draft = ScriptDraft("alice", executable = "/bin/job", environment = "invalid line", timeout = "")
        model.updateDraft(draft)
        val release = CompletableDeferred<Unit>()
        submission = { release.await(); ApiResult.Problem(403, "denied", null) }
        model.submit(request()); runCurrent()
        model.updateDraft(draft.copy(executable = "changed")); model.closeEditor()
        assertEquals(draft, model.state.value.draft)
        release.complete(Unit); runCurrent()
        assertEquals(draft, model.state.value.draft)
        assertFalse(model.state.value.loading)
        model.updateDraft(draft.copy(executable = "/corrected"))
        assertEquals("/corrected", model.state.value.draft?.executable)
        model.closeEditor()
        assertNull(model.state.value.draft)
    }

    @Test fun `new account removes old draft and opens only its own default identity`() = runTest {
        model.load(login()); runCurrent(); model.openEditor()
        model.updateDraft(ScriptDraft("other", executable = "/private/job", adminName = "admin"))
        val next = login("bob"); runCurrent()
        assertNull(model.state.value.draft)
        model.load(next); runCurrent(); model.openEditor()
        assertEquals(ScriptDraft("bob"), model.state.value.draft)
    }

    @Test fun `dirty draft includes invalid raw fields and returns clean after restoring defaults`() {
        val original = ScriptDraft("alice")
        assertFalse(original.dirty("alice"))
        assertTrue(original.copy(timeout = "").dirty("alice"))
        assertTrue(original.copy(environment = "bad entry").dirty("alice"))
        assertTrue(original.copy(adminName = " ").dirty("alice"))
        assertTrue(original.copy(runAs = "").dirty("alice"))
        assertFalse(original.copy(timeout = "").copy(timeout = "300").dirty("alice"))
    }

    @Test fun `unknown submission is recovered only by reading its exact request ID`() = runTest {
        model.load(login()); runCurrent(); model.openEditor()
        model.updateDraft(ScriptDraft("alice", executable = "/bin/job"))
        model.submit(request()); runCurrent()
        val pending = requireNotNull(model.state.value.pending)
        assertEquals(submittedKey, pending.taskId)
        val lookedUp = mutableListOf<String>()
        details = { id -> lookedUp += id; ApiResult.Success(ScriptTaskResult(true, "", task(id, "succeeded"))) }
        model.verifyRequest(); runCurrent()
        assertEquals(listOf(submittedKey), lookedUp)
        assertNull(model.state.value.pending); assertNull(model.state.value.draft)
        assertEquals(submittedKey, model.state.value.selected?.id)
        assertFalse(model.state.value.error); assertFalse(model.state.value.loading)
        assertEquals(1, submissions)
    }

    @Test fun `missing mismatched and failed recovery preserve original pending request and draft`() = runTest {
        model.load(login()); runCurrent(); model.openEditor()
        model.updateDraft(ScriptDraft("alice", executable = "/bin/job"))
        model.submit(request()); runCurrent()
        val pending = model.state.value.pending; val draft = model.state.value.draft
        for (result in listOf(ApiResult.Success(ScriptTaskResult(false, "guardian.script_not_found", null)),
            ApiResult.Success(ScriptTaskResult(true, "", task("other"))), ApiResult.Transport(null))) {
            details = { result }
            model.verifyRequest(); runCurrent()
            assertEquals(pending, model.state.value.pending); assertEquals(draft, model.state.value.draft)
            assertEquals("scripts.write_unknown", model.state.value.problemCode)
            assertFalse(model.state.value.loading)
        }
        assertEquals(1, submissions)
    }

    @Test fun `list refresh cannot unlock unknown request when exact lookup is missing`() = runTest {
        val owner = login(); model.load(owner); runCurrent(); model.openEditor()
        model.submit(request()); runCurrent(); val pending = model.state.value.pending
        model.closeEditor()
        details = { ApiResult.Success(ScriptTaskResult(false, "guardian.script_not_found", null)) }
        model.load(owner); runCurrent(); model.openEditor(); model.cancel("a"); runCurrent()
        assertEquals(pending, model.state.value.pending)
        assertNull(model.state.value.draft)
        assertEquals("scripts.write_unknown", model.state.value.problemCode)
        assertEquals(1, submissions); assertEquals(0, cancellations)
    }

    @Test fun `unknown cancellation remains pending while running and resolves on cancelled state`() = runTest {
        model.load(login()); runCurrent(); model.select("a"); runCurrent()
        model.cancel("a"); runCurrent()
        assertEquals(PendingScriptRequest("a", true), model.state.value.pending)
        model.verifyRequest(); runCurrent()
        assertNotNull(model.state.value.pending)
        model.cancel("a"); runCurrent(); assertEquals(1, cancellations)
        details = { ApiResult.Success(ScriptTaskResult(true, "", task(it, "cancelled"))) }
        model.verifyRequest(); runCurrent()
        assertNull(model.state.value.pending)
        assertEquals("cancelled", model.state.value.selected?.state)
        assertEquals(1, cancellations)
    }

    @Test fun `server error and agent timeout preserve request ID instead of granting replay`() = runTest {
        model.load(login()); runCurrent(); model.openEditor()
        submission = { ApiResult.Problem(500, "server.error", null) }
        model.submit(request()); runCurrent()
        assertEquals(submittedKey, model.state.value.pending?.taskId)
        model.submit(request()); runCurrent(); assertEquals(1, submissions)
        val next = login("bob"); runCurrent(); model.load(next); runCurrent(); model.openEditor()
        submission = { ApiResult.Success(ScriptTaskResult(false, "guardian.agent_timeout", null)) }
        model.submit(request()); runCurrent()
        assertEquals(submittedKey, model.state.value.pending?.taskId)
        assertEquals("scripts.write_unknown", model.state.value.problemCode)
    }

    @Test fun `recreated model recovers persisted request ID with no new submission`() = runTest {
        val owner = login(); model.load(owner); runCurrent(); model.openEditor()
        model.submit(request()); runCurrent(); val pending = model.state.value.pending
        store.clear(); model = newModel(); store.put("recreated", model)
        details = { ApiResult.Success(ScriptTaskResult(false, "guardian.script_not_found", null)) }
        model.load(owner); runCurrent()
        assertEquals(pending, model.state.value.pending)
        assertNull(model.state.value.draft)
        model.openEditor(); assertNull(model.state.value.draft)
        details = { ApiResult.Success(ScriptTaskResult(true, "", task(it, "succeeded"))) }
        model.verifyRequest(); runCurrent()
        assertNull(model.state.value.pending)
        assertNull(ScriptRequestJournal(storage).pending(owner))
        assertEquals(1, submissions)
    }

    @Test fun `journal write failure prevents sending and clears approval before retry`() = runTest {
        val owner = login(); model.load(owner); runCurrent(); model.openEditor()
        storage.failWrite = true
        val password = "secret".toCharArray()
        model.submit(request(password)); runCurrent()
        assertEquals(0, submissions)
        assertTrue(password.all { it == '\u0000' })
        assertFalse(model.state.value.journalAvailable)
        assertNotNull(model.state.value.draft)
        model.submit(request()); runCurrent(); assertEquals(0, submissions)
        storage.failWrite = false; model.verifyRequest(); runCurrent()
        assertTrue(model.state.value.journalAvailable)
        model.submit(request()); runCurrent(); assertEquals(1, submissions)
        assertNotNull(ScriptRequestJournal(storage).pending(owner))
    }

    @Test fun `journal read failure blocks new writes and later restores original pending ID`() = runTest {
        val owner = login(); model.load(owner); runCurrent(); model.openEditor()
        model.submit(request()); runCurrent(); val pending = model.state.value.pending
        store.clear(); model = newModel(); store.put("recreated", model)
        storage.failRead = true; model.load(owner); runCurrent(); model.openEditor(); model.cancel("a")
        assertFalse(model.state.value.journalAvailable)
        assertEquals(0, cancellations); assertNull(model.state.value.draft)
        storage.failRead = false
        details = { ApiResult.Transport(null) }
        model.verifyRequest(); runCurrent()
        assertEquals(pending, model.state.value.pending)
        assertEquals(1, submissions)
    }

    @Test fun `failure to clear accepted journal keeps request blocked until verified cleanup succeeds`() = runTest {
        val owner = login(); model.load(owner); runCurrent(); model.openEditor()
        submission = { storage.failWrite = true; ApiResult.Success(ScriptTaskResult(true, "", task(submittedKey))) }
        model.submit(request()); runCurrent()
        assertNotNull(model.state.value.pending); assertNotNull(model.state.value.draft)
        assertFalse(model.state.value.journalAvailable); assertFalse(model.state.value.loading)
        storage.failWrite = false
        details = { ApiResult.Success(ScriptTaskResult(true, "", task(it))) }
        model.verifyRequest(); runCurrent()
        assertNull(model.state.value.pending); assertNull(model.state.value.draft)
        assertNull(ScriptRequestJournal(storage).pending(owner))
        assertEquals(1, submissions)
    }
}
