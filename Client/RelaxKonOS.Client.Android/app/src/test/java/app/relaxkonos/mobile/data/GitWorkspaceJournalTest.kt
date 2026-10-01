package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class GitWorkspaceJournalTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes }
    }
    private suspend fun owner(): SessionState.Active {
        val gateway = FakeGateway(); gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        val session = AuthSession(gateway); session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `durable markers are isolated by service and account and disallow duplicate requests`() = runTest {
        val storage = Storage(); val journal = GitWorkspaceJournal(storage); val owner = owner(); val id = "11111111-1111-1111-1111-111111111111"
        val record = journal.begin(owner, id, GitAction.Push)
        assertEquals(listOf(record), GitWorkspaceJournal(storage).pending(owner))
        assertTrue(journal.pending(owner.copy(userName = "other")).isEmpty())
        assertTrue(journal.pending(owner.copy(serviceId = "other")).isEmpty())
        assertTrue(runCatching { journal.begin(owner, id, GitAction.Pull) }.isFailure)
        journal.complete(record); assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `corrupt storage prevents creation instead of forgetting an unknown operation`() = runTest {
        val owner = owner(); val storage = Storage().apply { bytes = byteArrayOf(1, 2, 3) }; val journal = GitWorkspaceJournal(storage)
        assertTrue(runCatching { journal.pending(owner) }.isFailure)
        assertTrue(runCatching { journal.begin(owner, "11111111-1111-1111-1111-111111111111", GitAction.Fetch) }.isFailure)
        assertArrayEquals(byteArrayOf(1, 2, 3), storage.bytes)
    }
}
