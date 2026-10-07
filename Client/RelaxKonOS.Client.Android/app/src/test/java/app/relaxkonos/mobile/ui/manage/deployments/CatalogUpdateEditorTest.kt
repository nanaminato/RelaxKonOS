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
class CatalogUpdateEditorTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val repository = DeploymentRepository(gateway, session)
    private val baseline = deploymentFixture()
    private suspend fun login(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://host.test"), "alice", "p".toCharArray()) {}
        return session.state.value as SessionState.Active
    }

    @Test fun `unexpected read failure preserves baseline and ends busy state`() = runTest {
        val owner = login()
        gateway.onDeploymentSnapshot = { _, _, _ -> throw IllegalStateException("private detail") }
        val editor = CatalogUpdateEditor(session, repository, owner, baseline, emptyList(), this, StandardTestDispatcher(testScheduler))
        try {
            editor.readCurrent(); advanceUntilIdle()
            assertEquals(baseline, editor.baseline)
            assertEquals(ApiResult.Transport(null), editor.result)
            assertFalse(editor.busy); assertEquals(0, editor.reload)
        } finally { editor.close() }
    }

    @Test fun `account change during read cannot replace baseline`() = runTest {
        val owner = login()
        gateway.onDeploymentSnapshot = { _, _, _ ->
            session.clearSession()
            ApiResult.Success(DeploymentSnapshot(baseline.copy(name = "other-account"), emptyList(), emptyList(), null))
        }
        val editor = CatalogUpdateEditor(session, repository, owner, baseline, emptyList(), this, StandardTestDispatcher(testScheduler))
        try {
            editor.readCurrent(); advanceUntilIdle()
            assertEquals(baseline, editor.baseline)
            assertNull(editor.result); assertFalse(editor.busy); assertEquals(0, editor.reload)
        } finally { editor.close() }
    }

    @Test fun `preview exception becomes safe feedback instead of indefinite loading`() = runTest {
        val owner = login()
        val target = CatalogTemplate("1", baseline.catalogTemplateId!!, "2.0.0", "RelaxKonOS", "built-in", true,
            "website", "test", listOf("linux/amd64"), emptyList(), CatalogResources(null, null, null),
            emptyList(), emptyList(), 80, "/", "retain data", false)
        gateway.onPreviewCatalogUpdate = { _, _ -> throw IllegalStateException("private detail") }
        val editor = CatalogUpdateEditor(session, repository, owner, baseline, listOf(target), this, StandardTestDispatcher(testScheduler))
        try {
            editor.refreshPreview()
            assertEquals(ApiResult.Transport(null), editor.preview)
            assertEquals("2.0.0", editor.selectedVersion)
        } finally { editor.close() }
    }
}
