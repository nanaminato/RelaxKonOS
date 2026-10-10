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
    private fun template(version: String) = CatalogTemplate("1", baseline.catalogTemplateId!!, version, "RelaxKonOS", "built-in", true,
        "website", "test", listOf("linux/amd64"), emptyList(), CatalogResources(null, null, null), emptyList(), emptyList(), 80, "/", "retain data", false)
    private fun change(target: CatalogTemplate) = CatalogApplicationUpdatePreview(baseline.id, baseline.updatedAt, null, baseline.catalogTemplateVersion!!,
        target, "image:1", "image:2", "review", emptyList())
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
    @Test fun `a confirmation for an unselected target version cannot publish`() = runTest {
        val owner = login()
        val target = template("2.0.0")
        val reviewed = change(target)
        var sends = 0; var callbacks = 0
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(baseline, emptyList(), emptyList(), null)) }
        gateway.onPreviewCatalogUpdate = { _, _ -> ApiResult.Success(reviewed) }
        gateway.onUpdateCatalog = { _, _ -> sends++; ApiResult.Success(DeploymentOperation("review-operation", baseline.id, "deploy", "queued", "preflight", null, null, null, 0, true)) }
        val editor = CatalogUpdateEditor(session, repository, owner, baseline, listOf(target, template("3.0.0")), this, StandardTestDispatcher(testScheduler))
        try {
            editor.preview = ApiResult.Success(reviewed)
            editor.selectedVersion = "3.0.0"
            editor.submit(reviewed) { callbacks++ }; advanceUntilIdle()
            assertEquals(0, sends); assertEquals(0, callbacks); assertNull(editor.result); assertFalse(editor.busy)
        } finally { editor.close() }
    }
    @Test fun `identical preview and current read failures each produce a fresh feedback event`() = runTest {
        val owner = login()
        gateway.onPreviewCatalogUpdate = { _, _ -> ApiResult.Transport(null) }
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Transport(null) }
        val editor = CatalogUpdateEditor(session, repository, owner, baseline, listOf(template("2.0.0")), this, StandardTestDispatcher(testScheduler))
        try {
            repeat(3) { index ->
                editor.refreshPreview()
                editor.readCurrent(); advanceUntilIdle()
                assertEquals((index + 1).toLong(), editor.previewVersion)
                assertEquals((index + 1).toLong(), editor.feedbackVersion)
                assertEquals(ApiResult.Transport(null), editor.preview); assertEquals(ApiResult.Transport(null), editor.result)
                assertEquals(baseline, editor.baseline); assertEquals("2.0.0", editor.selectedVersion)
            }
        } finally { editor.close() }
    }
    @Test fun `unknown update blocks model resubmission and preserves the target`() = runTest {
        val owner = login()
        val target = template("2.0.0"); val reviewed = change(target)
        var sends = 0; var callbacks = 0
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(baseline, emptyList(), emptyList(), null)) }
        gateway.onPreviewCatalogUpdate = { _, _ -> ApiResult.Success(reviewed) }
        gateway.onUpdateCatalog = { _, _ -> sends++; ApiResult.Transport(null) }
        val editor = CatalogUpdateEditor(session, repository, owner, baseline, listOf(target), this, StandardTestDispatcher(testScheduler))
        try {
            editor.refreshPreview(); editor.submit(reviewed) { callbacks++ }; advanceUntilIdle()
            assertTrue(editor.unknown); val version = editor.feedbackVersion
            editor.submit(reviewed) { callbacks++ }; advanceUntilIdle()
            assertEquals(version, editor.feedbackVersion); assertEquals(1, sends); assertEquals(0, callbacks)
            assertEquals("2.0.0", editor.selectedVersion); assertEquals(baseline, editor.baseline)
        } finally { editor.close() }
    }
}
