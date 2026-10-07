package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.DeploymentRepository
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class DeploymentDefinitionEditorTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val repository = DeploymentRepository(gateway, session)
    private val baseline = deploymentFixture()
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://host.test"), "alice", "p".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private fun edit(editor: DeploymentDefinitionEditor) {
        editor.draft.name = "edited-name"
        editor.configName = "NEW_VALUE"
        editor.configValue = "unsaved-value"
        editor.volumeName = "unsaved-volume"
    }
    private fun assertRetained(editor: DeploymentDefinitionEditor) {
        assertEquals("edited-name", editor.draft.name)
        assertEquals("NEW_VALUE", editor.configName)
        assertEquals("unsaved-value", editor.configValue)
        assertEquals("unsaved-volume", editor.volumeName)
        assertTrue(editor.dirty)
        assertFalse(editor.busy)
    }

    @Test fun `read rejection and transport failure preserve all unsubmitted input`() = runTest {
        val owner = login()
        for (failure in listOf(ApiResult.Problem(403, "application-deployment.permission_denied", null), ApiResult.Transport(null))) {
            gateway.onDeploymentSnapshot = { _, _, _ -> failure }
            val editor = DeploymentDefinitionEditor(session, repository, owner, baseline, this, StandardTestDispatcher(testScheduler))
            try { edit(editor); editor.loadCurrent(); advanceUntilIdle(); assertRetained(editor); assertEquals(failure, editor.result) }
            finally { editor.close() }
        }
    }

    @Test fun `unexpected read failure preserves input and becomes a safe transport result`() = runTest {
        val owner = login()
        gateway.onDeploymentSnapshot = { _, _, _ -> throw IllegalStateException("private detail") }
        val editor = DeploymentDefinitionEditor(session, repository, owner, baseline, this, StandardTestDispatcher(testScheduler))
        try { edit(editor); editor.loadCurrent(); advanceUntilIdle(); assertRetained(editor); assertEquals(ApiResult.Transport(null), editor.result) }
        finally { editor.close() }
    }

    @Test fun `only a matching successful read replaces the draft and clears unsubmitted input`() = runTest {
        val owner = login()
        val current = baseline.copy(name = "server-name")
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(current, emptyList(), emptyList(), null)) }
        val editor = DeploymentDefinitionEditor(session, repository, owner, baseline, this, StandardTestDispatcher(testScheduler))
        try {
            edit(editor); editor.loadCurrent(); advanceUntilIdle()
            assertEquals("server-name", editor.draft.name)
            assertEquals("", editor.configName); assertEquals("", editor.configValue); assertEquals("", editor.volumeName)
            assertFalse(editor.dirty); assertTrue(editor.loadedCurrent); assertNull(editor.result)
        } finally { editor.close() }
    }

    @Test fun `response for another application cannot replace or clear the draft`() = runTest {
        val owner = login()
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(baseline.copy(id = "another-application"), emptyList(), emptyList(), null)) }
        val editor = DeploymentDefinitionEditor(session, repository, owner, baseline, this, StandardTestDispatcher(testScheduler))
        try { edit(editor); editor.loadCurrent(); advanceUntilIdle(); assertRetained(editor); assertEquals(ApiResult.Transport(null), editor.result) }
        finally { editor.close() }
    }
}
