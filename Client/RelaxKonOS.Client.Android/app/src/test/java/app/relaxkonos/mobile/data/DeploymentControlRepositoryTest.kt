package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.*
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test

class DeploymentControlRepositoryTest {
    @Test fun `unknown cancellation retries the exact original operation and key`() = runTest {
        val owner = owner()
        val requests = mutableListOf<Pair<String, String>>()
        gateway.onCancelDeploymentOperation = { _, _, operationId, key ->
            requests.add(operationId to key)
            if (requests.size == 1) ApiResult.Transport(null)
            else ApiResult.Success(DeploymentOperation(operationId, "review-app", "deploy", "cancelled", "cancelled", null, null, null, null, false))
        }
        repository.cancel(owner, "review-app", "original-operation", "original-key")
        repository.cancel(owner, "review-app", "other-operation", "new-key")
        assertEquals(1, requests.size)
        assertTrue(repository.retryControl(owner, "review-app") is ApiResult.Success)
        assertEquals(listOf("original-operation" to "original-key", "original-operation" to "original-key"), requests)
        assertNull(repository.pendingControl(owner, "review-app"))
    }
    @Test fun `cancellation receipt for another operation retains the original request`() = runTest {
        val owner = owner()
        gateway.onCancelDeploymentOperation = { _, _, _, _ ->
            ApiResult.Success(DeploymentOperation("other-operation", "review-app", "deploy", "cancelled", "cancelled", null, null, null, null, false))
        }
        assertEquals(ApiResult.Transport(null), repository.cancel(owner, "review-app", "original-operation", "original-key"))
        assertEquals("original-operation", repository.pendingControl(owner, "review-app")?.argument)
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val repository = DeploymentRepository(gateway, session)
    private suspend fun owner(): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://control-review.invalid"), "review", "test".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `lost rollback receipt blocks a fresh key and a different control`() = runTest {
        val owner = owner(); var sends = 0
        gateway.onRollbackDeployment = { _, _, _, _, _ -> sends++; ApiResult.Transport(null) }
        gateway.onDeleteDeployment = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        repository.rollback(owner, "review-app", "review-revision", "original-key")
        repository.rollback(owner, "review-app", "review-revision", "fresh-key")
        repository.delete(owner, "review-app", "delete-key")
        assertEquals(1, sends)
    }
    @Test fun `unexpected dispatched exception returns safe failure`() = runTest {
        val owner = owner()
        gateway.onDeploymentLifecycle = { _, _, _, _, _ -> throw IllegalStateException("private provider detail") }
        assertEquals(ApiResult.Transport(null), repository.lifecycle(owner, "review-app", DeploymentLifecycleAction.Restart, "original-key"))
    }
    @Test fun `recreated repository retries persisted original action and key`() = runTest {
        val owner = owner(); val storage = MemoryDeploymentControlStorage()
        val first = DeploymentRepository(gateway, session, controlJournal = DeploymentControlJournal(storage))
        val keys = mutableListOf<String>()
        gateway.onDeploymentLifecycle = { _, _, id, action, key ->
            assertEquals(DeploymentLifecycleAction.Stop, action); keys.add(key)
            if (keys.size == 1) ApiResult.Transport(null)
            else ApiResult.Success(DeploymentOperation("review-operation", id, "stop", "queued", "queued", null, null, null, null, true))
        }
        first.lifecycle(owner, "review-app", DeploymentLifecycleAction.Stop, "original-key")
        val restored = DeploymentRepository(gateway, session, controlJournal = DeploymentControlJournal(storage))
        assertNotNull(restored.pendingControl(owner, "review-app"))
        assertTrue(restored.retryControl(owner, "review-app") is ApiResult.Success)
        assertEquals(listOf("original-key", "original-key"), keys)
        assertNull(restored.pendingControl(owner, "review-app"))
    }
    @Test fun `definitive initial rejection clears key but retry rejection does not erase unknown`() = runTest {
        val owner = owner()
        gateway.onDeleteDeployment = { _, _, _, _ -> ApiResult.Problem(403, "denied", null) }
        repository.delete(owner, "review-app", "rejected-key")
        assertNull(repository.pendingControl(owner, "review-app"))
        gateway.onDeleteDeployment = { _, _, _, _ -> ApiResult.Transport(null) }
        repository.delete(owner, "review-app", "unknown-key")
        gateway.onDeleteDeployment = { _, _, _, _ -> ApiResult.Problem(404, "missing", null) }
        repository.retryControl(owner, "review-app")
        assertEquals("unknown-key", repository.pendingControl(owner, "review-app")?.key)
    }
    @Test fun `foreign successful receipt cannot complete pending request`() = runTest {
        val owner = owner()
        gateway.onRollbackDeployment = { _, _, _, _, _ -> ApiResult.Success(DeploymentOperation("review-operation", "other-app", "rollback", "queued", "queued", null, null, null, null, true)) }
        assertEquals(ApiResult.Transport(null), repository.rollback(owner, "review-app", "review-revision", "original-key"))
        assertNotNull(repository.pendingControl(owner, "review-app"))
    }
    @Test fun `storage failure prevents sending and corrupt records are not replaced`() = runTest {
        val owner = owner(); var sends = 0
        gateway.onDeleteDeployment = { _, _, _, _ -> sends++; ApiResult.Transport(null) }
        for (corrupt in listOf(false, true)) {
            val storage = object : DeploymentControlStorage {
                override fun read(): ByteArray? = if (corrupt) "corrupt".toByteArray() else null
                override fun write(bytes: ByteArray) { error("storage unavailable") }
            }
            val guarded = DeploymentRepository(gateway, session, controlJournal = DeploymentControlJournal(storage))
            assertEquals(ApiResult.Transport(null), guarded.delete(owner, "review-app", "new-key"))
        }
        assertEquals(0, sends)
    }
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    @Test fun `cancelled dispatched request retains durable reference`() = runTest {
        val owner = owner(); val storage = MemoryDeploymentControlStorage()
        val guarded = DeploymentRepository(gateway, session, controlJournal = DeploymentControlJournal(storage))
        gateway.onDeleteDeployment = { _, _, _, _ -> awaitCancellation() }
        val job = launch { guarded.delete(owner, "review-app", "original-key") }
        runCurrent(); job.cancelAndJoin()
        assertEquals("original-key", DeploymentControlJournal(storage).pending(owner, "review-app")?.key)
    }
}
