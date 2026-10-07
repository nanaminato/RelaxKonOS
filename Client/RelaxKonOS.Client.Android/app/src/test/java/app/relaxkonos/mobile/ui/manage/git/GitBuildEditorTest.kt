package app.relaxkonos.mobile.ui.manage.git

import androidx.compose.runtime.saveable.SaverScope
import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
class GitBuildEditorTest {
    private val base = FakeGateway()
    private val fixed = GitBuildResolved("https://example.test/repo.git", "main", "a".repeat(40))
    private fun build(id: String, state: String = "running") = GitBuildOperation(
        id, fixed.repositoryUrl, fixed.reference, fixed.commitSha, ".", "Dockerfile", state,
        null, null, null, emptyList(),
    )
    private var records = listOf(build("first"), build("linked"))
    private var starts = 0
    private var polls = 0
    private var onPoll: suspend (String) -> ApiResult<GitBuildOperation> = { ApiResult.Success(build(it, "succeeded")) }
    private var onRefs: suspend () -> ApiResult<List<GitBuildRef>> = { ApiResult.Success(emptyList()) }
    private val gateway = object : RelaxKonGateway by base {
        override suspend fun gitBuildCredentials(serverUrl: String, accessToken: String) = ApiResult.Success(emptyList<GitBuildCredential>())
        override suspend fun gitBuilds(serverUrl: String, accessToken: String) = ApiResult.Success(records)
        override suspend fun gitBuildRefs(serverUrl: String, accessToken: String, url: String, credentialId: String?) = onRefs()
        override suspend fun gitBuildStart(serverUrl: String, accessToken: String, request: GitBuildRequest, key: String): ApiResult<GitBuildOperation> {
            starts++
            return ApiResult.Transport("response lost")
        }
        override suspend fun gitBuildGet(serverUrl: String, accessToken: String, id: String): ApiResult<GitBuildOperation> {
            polls++
            return onPoll(id)
        }
    }
    private val session = AuthSession(gateway)
    private val index = OperationIndex(object : OperationIndexStorage {
        override fun read(): ByteArray? = null
        override fun write(bytes: ByteArray) = Unit
    })
    private val git = GitRepositoryClient(gateway, session, index)
    private val deployments = DeploymentRepository(gateway, session, index)
    private suspend fun owner(): SessionState.Active {
        base.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }

    @Test fun `poll failures retain last build and expose safe feedback`() = runTest {
        val owner = owner()
        for (throws in listOf(false, true)) {
            onPoll = { if (throws) throw IllegalStateException("private detail") else ApiResult.Transport("private detail") }
            val editor = GitBuildEditor(git, deployments, owner, { true }, this)
            try {
                editor.builds = records; editor.selectedBuildId = "first"
                editor.observeSelected(); advanceTimeBy(2501); runCurrent()
                assertEquals("transport", editor.problem)
                assertEquals("running", editor.selected?.state)
            } finally { editor.close() }
        }
    }

    @Test fun `deep link selects matching build after load and later refresh preserves user selection`() = runTest {
        val owner = owner()
        val editor = GitBuildEditor(git, deployments, owner, { session.state.value === owner }, this)
        editor.open("linked")
        editor.load()
        runCurrent()
        assertEquals("linked", editor.selectedBuildId)
        editor.selectedBuildId = "first"
        editor.refresh()
        runCurrent()
        assertEquals("first", editor.selectedBuildId)
        editor.close()
    }

    @Test fun `duplicate submission is gated and ambiguous retry reuses the idempotency key`() = runTest {
        val owner = owner()
        val editor = GitBuildEditor(git, deployments, owner, { true }, this)
        editor.startBuild(fixed)
        editor.startBuild(fixed)
        runCurrent()
        assertEquals(1, starts)
        val key = editor.buildKey
        assertNotNull(key)
        editor.startBuild(fixed)
        runCurrent()
        assertEquals(2, starts)
        assertEquals(key, editor.buildKey)
        assertFalse(editor.active)
        editor.close()
    }

    @Test fun `late response after owner change is discarded and closing cancels polling`() = runTest {
        val owner = owner()
        var current = true
        val response = CompletableDeferred<ApiResult<List<GitBuildRef>>>()
        onRefs = { response.await() }
        val editor = GitBuildEditor(git, deployments, owner, { current }, this)
        editor.listRefs()
        runCurrent()
        current = false
        response.complete(ApiResult.Success(listOf(GitBuildRef("late", fixed.commitSha))))
        runCurrent()
        assertTrue(editor.remoteRefs.isEmpty())
        current = true
        editor.builds = records
        editor.selectedBuildId = "first"
        editor.observeSelected()
        runCurrent()
        editor.credentialToken = "secret"
        editor.close()
        advanceTimeBy(5000)
        runCurrent()
        assertEquals(0, polls)
        assertEquals("", editor.credentialToken)
    }

    @Test fun `saved form retains operation keys but never serializes credential plaintext`() = runTest {
        val owner = owner()
        val editor = GitBuildEditor(git, deployments, owner, { true }, this)
        editor.url = fixed.repositoryUrl
        editor.buildKey = "retry-key"
        editor.publishKey = "publish-key"
        editor.credentialToken = "secret"
        val saver = GitBuildEditor.saver(git, deployments, owner, { true }, this)
        val saved = with(saver) {
            object : SaverScope { override fun canBeSaved(value: Any) = true }.save(editor)
        }
        assertNotNull(saved)
        val restored = saver.restore(saved!!)!!
        assertEquals(editor.url, restored.url)
        assertEquals("retry-key", restored.buildKey)
        assertEquals("publish-key", restored.publishKey)
        assertEquals("", restored.credentialToken)
        editor.close()
        restored.close()
    }
}
