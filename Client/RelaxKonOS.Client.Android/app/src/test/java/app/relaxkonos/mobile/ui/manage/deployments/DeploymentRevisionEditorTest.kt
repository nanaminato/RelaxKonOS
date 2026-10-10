package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.DeploymentRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class DeploymentRevisionEditorTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val repository = DeploymentRepository(gateway, session)
    private val baseline = deploymentFixture()
    private val snapshot = DeploymentSnapshot(baseline, emptyList(), emptyList(), null)
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://revision-review.invalid"), "review", "test".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private fun edit(editor: DeploymentRevisionEditor) {
        editor.image = "nginx:1.27.3-alpine"; editor.baseImage = "base:1"; editor.runtime = "runtime"
        editor.entry = "program"; editor.selfContained = true; editor.arguments.addAll(listOf("", " exact value "))
    }
    private fun retained(editor: DeploymentRevisionEditor) {
        assertEquals("nginx:1.27.3-alpine", editor.image); assertEquals("base:1", editor.baseImage)
        assertEquals("runtime", editor.runtime); assertEquals("program", editor.entry)
        assertTrue(editor.selfContained); assertEquals(listOf("", " exact value "), editor.arguments.toList())
    }
    @Test fun `unexpected reload failure preserves every source field and archive`() = runTest {
        val owner = login()
        gateway.onDeploymentSnapshot = { _, _, _ -> throw IllegalStateException("private read detail") }
        val editor = DeploymentRevisionEditor(session, repository, owner, snapshot, this, StandardTestDispatcher(testScheduler))
        var clears = 0
        try {
            edit(editor); editor.reload { clears++ }; advanceUntilIdle()
            retained(editor); assertEquals(0, clears); assertFalse(editor.busy)
            assertEquals(ApiResult.Transport(null), editor.result)
        } finally { editor.close() }
    }
    @Test fun `busy reload is not duplicated and only matching successful facts clear source and archive`() = runTest {
        val owner = login()
        val response = CompletableDeferred<ApiResult<DeploymentSnapshot>>()
        var reads = 0; var clears = 0
        gateway.onDeploymentSnapshot = { _, _, _ -> reads++; response.await() }
        val editor = DeploymentRevisionEditor(session, repository, owner, snapshot, this, StandardTestDispatcher(testScheduler))
        try {
            edit(editor); editor.reload { clears++ }; editor.reload { clears++ }; runCurrent()
            retained(editor); assertTrue(editor.busy)
            response.complete(ApiResult.Success(snapshot)); advanceUntilIdle()
            assertEquals(1, reads); assertEquals(1, clears)
            assertEquals("", editor.image); assertEquals("", editor.entry); assertFalse(editor.selfContained)
            assertTrue(editor.arguments.isEmpty()); assertFalse(editor.busy)
        } finally { editor.close() }
    }
    @Test fun `rejected reload for another application preserves the source`() = runTest {
        val owner = login()
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(snapshot.copy(application = baseline.copy(id = "other"))) }
        val editor = DeploymentRevisionEditor(session, repository, owner, snapshot, this, StandardTestDispatcher(testScheduler))
        var clears = 0
        try {
            edit(editor); editor.reload { clears++ }; advanceUntilIdle()
            retained(editor); assertEquals(0, clears); assertEquals(ApiResult.Transport(null), editor.result)
        } finally { editor.close() }
    }
    @Test fun `repeated failures have distinct feedback and unknown submit cannot clear archive or replay`() = runTest {
        val owner = login()
        var sends = 0; var clears = 0; var accepted = 0
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(snapshot) }
        gateway.onDeployRevision = { _, _, _, _ -> sends++; throw IllegalStateException("private send detail") }
        val editor = DeploymentRevisionEditor(session, repository, owner, snapshot, this, StandardTestDispatcher(testScheduler))
        try {
            edit(editor)
            val source = DeploymentRevisionSource(imageReference = editor.image)
            editor.submit(source, { clears++ }, { accepted++ }); advanceUntilIdle()
            assertTrue(editor.unknown); assertEquals(1, sends); assertEquals(ApiResult.Transport(null), editor.result)
            val submittedVersion = editor.feedbackVersion
            editor.submit(source, { clears++ }, { accepted++ }); advanceUntilIdle()
            assertEquals(submittedVersion, editor.feedbackVersion); assertEquals(1, sends)
            gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Transport(null) }
            repeat(2) { index ->
                editor.reload { clears++ }; advanceUntilIdle()
                retained(editor); assertTrue(editor.unknown); assertEquals(submittedVersion + index + 1, editor.feedbackVersion)
                assertEquals(0, clears); assertEquals(0, accepted); assertEquals(1, sends)
            }
        } finally { editor.close() }
    }
}
