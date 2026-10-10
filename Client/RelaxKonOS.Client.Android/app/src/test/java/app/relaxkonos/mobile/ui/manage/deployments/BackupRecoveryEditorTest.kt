package app.relaxkonos.mobile.ui.manage.deployments

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.data.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class BackupRecoveryEditorTest {
    private val authentication = FakeGateway()
    private val session = AuthSession(authentication)
    private val applicationId = "review-application"
    private val manifest = BackupManifest("review-backup", "review-operation", applicationId, "completed", "key-reference", emptyList(), null)
    private var onManifests: suspend () -> ApiResult<List<BackupManifest>> = { ApiResult.Success(listOf(manifest)) }
    private var onReconcile: suspend () -> ApiResult<BackupManifest?> = { ApiResult.Success(null) }
    private var reads = 0
    private var reconciliations = 0
    private var sends = 0
    private var onCreate: suspend () -> ApiResult<BackupManifest> = { ApiResult.Transport(null) }
    private val gateway = object : RelaxKonGateway by authentication {
        override suspend fun backupManifests(serverUrl: String, accessToken: String, applicationId: String): ApiResult<List<BackupManifest>> {
            reads++; return onManifests()
        }
        override suspend fun definitionBackupRequest(serverUrl: String, accessToken: String, applicationId: String, idempotencyKey: String): ApiResult<BackupManifest?> {
            reconciliations++; return onReconcile()
        }
        override suspend fun createDefinitionBackup(serverUrl: String, accessToken: String, applicationId: String, idempotencyKey: String): ApiResult<BackupManifest> {
            sends++; return onCreate()
        }
        override suspend fun backupPreflight(serverUrl: String, accessToken: String, backupId: String): ApiResult<BackupPreflight> = ApiResult.Transport(null)
    }
    private val journal = BackupRecoveryRequestJournal(object : BackupRecoveryRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    })
    private val repository = BackupRecoveryRepository(gateway, session, OperationIndex(object : OperationIndexStorage {
        override fun read(): ByteArray? = null
        override fun write(bytes: ByteArray) = Unit
    }), journal)
    private suspend fun login(): SessionState.Active {
        authentication.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://backup-review.invalid"), "review", "test".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `manifest exception cannot erase a persisted unknown request marker`() = runTest {
        val owner = login(); journal.begin(owner, applicationId)
        onManifests = { throw IllegalStateException("private list detail") }
        val editor = BackupRecoveryEditor(session, repository, owner, applicationId, this, StandardTestDispatcher(testScheduler))
        try {
            editor.load()
            assertTrue(editor.state.pendingRequest)
            advanceUntilIdle()
            assertTrue(editor.state.pendingRequest); assertFalse(editor.state.loading)
            assertEquals(ApiResult.Transport(null), editor.state.manifests); assertEquals(0, sends)
        } finally { editor.close() }
    }
    @Test fun `reconciliation exception cannot discard a successful manifest list`() = runTest {
        val owner = login(); journal.begin(owner, applicationId)
        onReconcile = { throw IllegalStateException("private reconcile detail") }
        val editor = BackupRecoveryEditor(session, repository, owner, applicationId, this, StandardTestDispatcher(testScheduler))
        try {
            editor.load(); advanceUntilIdle()
            assertEquals(ApiResult.Success(listOf(manifest)), editor.state.manifests)
            assertEquals(ApiResult.Transport(null), editor.state.reconciliation)
            assertTrue(editor.state.pendingRequest); assertFalse(editor.state.loading)
            assertEquals(1, reads); assertEquals(1, reconciliations); assertEquals(0, sends)
        } finally { editor.close() }
    }
    @Test fun `successful reconciliation merges exact facts and clears only the completed request`() = runTest {
        val owner = login(); journal.begin(owner, applicationId)
        val recovered = manifest.copy(backupId = "recovered-backup")
        onReconcile = { ApiResult.Success(recovered) }
        val editor = BackupRecoveryEditor(session, repository, owner, applicationId, this, StandardTestDispatcher(testScheduler))
        try {
            editor.load(); advanceUntilIdle()
            assertEquals(ApiResult.Success(listOf(recovered, manifest)), editor.state.manifests)
            assertFalse(editor.state.pendingRequest); assertNull(journal.pending(owner, applicationId))
            assertEquals(1, reconciliations); assertEquals(0, sends)
        } finally { editor.close() }
    }
    @Test fun `failed list does not prevent reconciliation or invent a complete inventory`() = runTest {
        val owner = login(); journal.begin(owner, applicationId)
        onManifests = { throw IllegalStateException("private list detail") }
        onReconcile = { ApiResult.Success(manifest) }
        val editor = BackupRecoveryEditor(session, repository, owner, applicationId, this, StandardTestDispatcher(testScheduler))
        try {
            editor.load(); advanceUntilIdle()
            assertEquals(ApiResult.Transport(null), editor.state.manifests)
            assertEquals(ApiResult.Success(manifest), editor.state.reconciliation)
            assertFalse(editor.state.pendingRequest); assertFalse(editor.state.loading)
            assertEquals(1, reconciliations); assertEquals(0, sends)
        } finally { editor.close() }
    }
    @Test fun `transport reconciliation retains list and pending key without replay`() = runTest {
        val owner = login(); val pending = journal.begin(owner, applicationId)
        onReconcile = { ApiResult.Transport(null) }
        val editor = BackupRecoveryEditor(session, repository, owner, applicationId, this, StandardTestDispatcher(testScheduler))
        try {
            editor.load(); advanceUntilIdle()
            assertEquals(ApiResult.Success(listOf(manifest)), editor.state.manifests)
            assertEquals(ApiResult.Transport(null), editor.state.reconciliation)
            assertTrue(editor.state.pendingRequest)
            assertEquals(pending, journal.pending(owner, applicationId)); assertEquals(0, sends)
        } finally { editor.close() }
    }
    @Test fun `session change during list read prevents reconciliation and old facts`() = runTest {
        val owner = login(); journal.begin(owner, applicationId)
        onManifests = { session.clearSession(); ApiResult.Success(listOf(manifest)) }
        val editor = BackupRecoveryEditor(session, repository, owner, applicationId, this, StandardTestDispatcher(testScheduler))
        try {
            editor.load(); advanceUntilIdle()
            assertNull(editor.state.manifests); assertEquals(0, reconciliations); assertEquals(0, sends)
        } finally { editor.close() }
    }
    @Test fun `successful retry clears stale reconciliation failure`() = runTest {
        val owner = login(); journal.begin(owner, applicationId)
        onReconcile = { ApiResult.Transport(null) }
        onCreate = { ApiResult.Success(manifest) }
        val editor = BackupRecoveryEditor(session, repository, owner, applicationId, this, StandardTestDispatcher(testScheduler))
        try {
            editor.load(); advanceUntilIdle()
            assertEquals(ApiResult.Transport(null), editor.state.reconciliation)
            editor.create(); advanceUntilIdle()
            assertFalse(editor.state.pendingRequest)
            assertNull(editor.state.reconciliation)
            assertEquals(ApiResult.Success(manifest), editor.state.creation)
            assertEquals(1, sends)
        } finally { editor.close() }
    }
    @Test fun `equal creation and preflight failures have distinct feedback events`() = runTest {
        val owner = login(); journal.begin(owner, applicationId)
        val editor = BackupRecoveryEditor(session, repository, owner, applicationId, this, StandardTestDispatcher(testScheduler))
        try {
            repeat(2) { attempt ->
                editor.create()
                assertEquals(attempt + 1L, editor.creationFeedbackVersion)
                editor.create()
                assertEquals(attempt + 1L, editor.creationFeedbackVersion)
                advanceUntilIdle()
                assertEquals(ApiResult.Transport(null), editor.state.creation)
                editor.select(manifest.backupId); advanceUntilIdle()
                assertEquals(attempt + 1L, editor.preflightFeedbackVersion)
                assertEquals(ApiResult.Transport(null), editor.state.preflight)
                assertTrue(editor.state.pendingRequest)
            }
            assertEquals(2, sends)
        } finally { editor.close() }
    }
}
