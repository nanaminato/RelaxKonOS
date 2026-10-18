package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Test
import org.junit.Assert.*

class WebsitePublicationRequestsTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        var fail = false
        override fun read() = bytes?.copyOf()
        override fun write(bytes: ByteArray) { check(!fail); this.bytes = bytes.copyOf() }
    }
    private val storage = Storage()
    private val journal = WebsitePublicationJournal(storage)
    private val delegate = FakeGateway()
    private val keys = mutableListOf<String>()
    private var historyReads = 0
    private var response: suspend () -> ApiResult<WebsitePublicationOperation> = { ApiResult.Transport(null) }
    private val operation = WebsitePublicationOperation("original", "app", "nginx", "example.test", null,
        null, null, "running", "verifying", "", null, emptyList(), null, null, null)
    private var recovered = operation
    private val gateway = object : RelaxKonGateway by delegate {
        override suspend fun publishWebsite(serverUrl: String, accessToken: String, request: WebsitePublishRequest,
            idempotencyKey: String): ApiResult<WebsitePublicationOperation> {
            keys += idempotencyKey
            return response()
        }
        override suspend fun websitePublicationHistory(serverUrl: String, accessToken: String, applicationId: String): ApiResult<List<WebsitePublicationOperation>> {
            historyReads++
            return ApiResult.Success(listOf(operation))
        }
        override suspend fun websitePublication(serverUrl: String, accessToken: String, operationId: String) = ApiResult.Success(recovered)
    }
    private val session = AuthSession(gateway)
    private val repository = WebPublishingRepository(gateway, session, ElevationRepository(gateway, session,
        CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), UsageMemoryStore(InMemoryUsageMemoryStorage())))
    private val requests = WebsitePublicationRequests(session, repository, journal)
    private val request = WebsitePublishRequest("app", "nginx", "example.test", contactEmail = "owner@example.test",
        acceptedTerms = true, publicReachabilityConfirmed = true, confirmed = true)
    private suspend fun login(account: String = "review"): SessionState.Active {
        delegate.onLogin = { _, _, _ -> ApiResult.Success(loginSession(userName = account)) }
        session.login(ServerConnectionIdentityRules.direct("https://publication.invalid"), account, "test".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `unknown result survives restart and explicit retry uses original key`() = runTest {
        val owner = login()
        requests.submit(owner, request, ElevationAnswerProvider.Declines)
        val pending = journal.pending(owner)!!
        assertTrue(pending.attempted)
        val restarted = WebsitePublicationRequests(session, repository, WebsitePublicationJournal(storage))
        response = { ApiResult.Success(operation) }
        assertTrue(restarted.submit(owner, request, ElevationAnswerProvider.Declines).result is ApiResult.Success)
        assertEquals(listOf(pending.key, pending.key), keys)
        assertEquals(1, historyReads)
        assertNull(journal.pending(owner))
    }
    @Test fun `changed draft never sends a second publication`() = runTest {
        val owner = login()
        requests.submit(owner, request, ElevationAnswerProvider.Declines)
        val result = requests.submit(owner, request.copy(domain = "different.test"), ElevationAnswerProvider.Declines)
        assertTrue(result.result is ApiResult.Problem)
        assertEquals(1, keys.size)
        assertNotNull(journal.pending(owner))
    }
    @Test fun `cancelled submission keeps original request`() = runTest {
        val owner = login()
        response = { throw CancellationException("disconnected") }
        try { requests.submit(owner, request, ElevationAnswerProvider.Declines); fail() } catch (_: CancellationException) {}
        assertTrue(journal.pending(owner)!!.attempted)
    }
    @Test fun `storage failure before attempt prevents remote mutation`() = runTest {
        val owner = login(); storage.fail = true
        try { requests.submit(owner, request, ElevationAnswerProvider.Declines); fail() } catch (_: IllegalStateException) {}
        assertTrue(keys.isEmpty())
    }
    @Test fun `another account cannot see or recover original request`() = runTest {
        val owner = login()
        requests.submit(owner, request, ElevationAnswerProvider.Declines)
        val pending = journal.pending(owner)!!
        val other = login("other")
        assertNull(journal.pending(other))
        assertTrue(requests.recover(other, pending, "original", true).result is ApiResult.Problem)
        assertNotNull(journal.pending(owner))
    }
    @Test fun `wrong task target cannot clear pending request`() = runTest {
        val owner = login()
        requests.submit(owner, request, ElevationAnswerProvider.Declines)
        recovered = operation.copy(applicationId = "other")
        assertTrue(requests.recover(owner, journal.pending(owner)!!, "original", true).result is ApiResult.Transport)
        assertNotNull(journal.pending(owner))
    }
    @Test fun `rejection after unknown attempt does not clear pending request`() = runTest {
        val owner = login()
        requests.submit(owner, request, ElevationAnswerProvider.Declines)
        response = { ApiResult.Problem(409, "rejected", null) }
        requests.submit(owner, request, ElevationAnswerProvider.Declines)
        assertNotNull(journal.pending(owner))
        assertEquals(keys[0], keys[1])
    }
    @Test fun `authoritative rejection survives failure to remove local request`() = runTest {
        val owner = login()
        val rejected = ApiResult.Problem(422, "website.invalid_domain", null)
        response = { storage.fail = true; rejected }
        val outcome = requests.submit(owner, request, ElevationAnswerProvider.Declines)
        assertEquals(rejected, outcome.result)
        assertTrue(outcome.storageFailed)
        assertNotNull(journal.pending(owner))
        assertEquals(1, keys.size)
    }

}
