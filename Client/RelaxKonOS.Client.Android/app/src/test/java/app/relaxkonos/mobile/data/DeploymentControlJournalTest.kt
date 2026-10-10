package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class DeploymentControlJournalTest {
    @Test fun `restored malformed keys and action arguments fail closed without overwriting`() = runTest {
        val owner = owner()
        for ((kind, argument, key) in listOf(
            Triple("Rollback", "", "key"), Triple("Cancel", " ", "key"),
            Triple("Delete", "unexpected-target", "key"), Triple("Start", "", "bad key"),
        )) {
            val bytes = org.json.JSONObject().put("version", 1).put("requests", org.json.JSONArray().put(
                org.json.JSONObject().put("serviceId", owner.serviceId).put("account", owner.userName)
                    .put("applicationId", "review-app").put("kind", kind).put("argument", argument).put("key", key)
            )).toString().toByteArray()
            var writes = 0
            val journal = DeploymentControlJournal(object : DeploymentControlStorage {
                override fun read() = bytes
                override fun write(bytes: ByteArray) { writes++ }
            })
            assertTrue("Invalid restored $kind request was accepted", runCatching { journal.pending(owner) }.isFailure)
            assertEquals(0, writes)
        }
    }
    @get:Rule val temporary = TemporaryFolder()
    @Test fun `invalid UTF8 is rejected instead of silently changing account identity`() = runTest {
        val owner = owner()
        val bytes = "{\"version\":1,\"requests\":[],\"unused\":\"".toByteArray() + byteArrayOf(0x80.toByte()) + "\"}".toByteArray()
        val journal = DeploymentControlJournal(object : DeploymentControlStorage {
            override fun read() = bytes
            override fun write(bytes: ByteArray) { fail("Corrupt storage must not be overwritten") }
        })
        assertTrue(runCatching { journal.pending(owner) }.isFailure)
    }
    private suspend fun owner(user: String = "review", url: String = "https://control-review.invalid"): SessionState.Active {
        val gateway = FakeGateway()
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession(userName = user)) }
        val session = AuthSession(gateway)
        session.login(ServerConnectionIdentityRules.direct(url), user, "test".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `atomic file storage restores exact references and isolates owners`() = runTest {
        val owner = owner(); val directory = temporary.newFolder()
        val journal = DeploymentControlJournal(FileDeploymentControlStorage(directory))
        val pending = PendingDeploymentControl(owner.serviceId, owner.userName, "review-app", DeploymentControlKind.Rollback, "review-revision", "review-key")
        journal.begin(pending)
        val restored = DeploymentControlJournal(FileDeploymentControlStorage(directory))
        assertEquals(pending, restored.pending(owner, pending.applicationId))
        assertNull(restored.pending(owner("other"), pending.applicationId))
        assertNull(restored.pending(owner(url = "https://other-review.invalid"), pending.applicationId))
        restored.complete(pending)
        assertNull(DeploymentControlJournal(FileDeploymentControlStorage(directory)).pending(owner, pending.applicationId))
    }
    @Test fun `unresolved key cannot be overwritten by a different action`() = runTest {
        val owner = owner(); val journal = DeploymentControlJournal(MemoryDeploymentControlStorage())
        val pending = PendingDeploymentControl(owner.serviceId, owner.userName, "review-app", DeploymentControlKind.Delete, "", "review-key")
        journal.begin(pending)
        try { journal.begin(pending.copy(kind = DeploymentControlKind.Start, key = "new-key")); fail("Unresolved request must not be replaced") }
        catch (_: IllegalStateException) { }
        assertEquals(pending, journal.pending(owner, pending.applicationId))
    }
}
