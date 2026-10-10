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
    @get:Rule val temporary = TemporaryFolder()
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
