package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import app.relaxkonos.mobile.ui.manage.websites.WebSiteDraft
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class WebSiteRepositoryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val storage = Storage()
    private val journal = WebSiteMutationJournal(storage)
    private val repository = WebSiteRepository(gateway, session,
        ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), app.relaxkonos.mobile.data.UsageMemoryStore(app.relaxkonos.mobile.data.InMemoryUsageMemoryStorage())), journal)
    private val site = WebPublishingWire.site(WEB_SITE)
    private val request = WebSiteDraft.from(site).request()!!
    private suspend fun signIn(): SessionState.Active {
        val login = loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.WEB_SERVER), privilegedOperations = true)) }
        gateway.onLogin = { _, _, _ -> ApiResult.Success(login) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `transport ambiguity freezes exact original intent and does not persist paths`() = runTest {
        val owner = signIn()
        gateway.onSaveWebSite = { _, _ -> ApiResult.Transport(null) }
        assertTrue(repository.save(owner, "nginx", request, ElevationAnswerProvider.Declines) is ApiResult.Transport)
        val pending = WebSiteMutationJournal(storage).pending(owner).single()
        assertTrue(pending.attempted)
        assertFalse(storage.bytes!!.decodeToString().contains("/srv/site"))
        assertTrue(runCatching { repository.save(owner, "nginx", request.copy(name = "different"), ElevationAnswerProvider.Declines) }.isFailure)
        assertEquals(pending.fingerprint, journal.pending(owner).single().fingerprint)
    }
    @Test fun `explicit retry reads facts and refuses changed version before another write`() = runTest {
        val owner = signIn()
        var writes = 0
        gateway.onSaveWebSite = { _, _ -> writes++; ApiResult.Transport(null) }
        repository.save(owner, "nginx", request, ElevationAnswerProvider.Declines)
        gateway.onWebSites = { ApiResult.Success(listOf(site.copy(updatedAt = "2026-09-30T00:00:00.1234568Z"))) }
        assertEquals("webserver.site_changed", (repository.save(owner, "nginx", request, ElevationAnswerProvider.Declines) as ApiResult.Problem).code)
        assertEquals(1, writes)
        assertEquals(1, journal.pending(owner).size)
    }
    @Test fun `initial conflict clears pending but uncertain delete never treats missing site as success`() = runTest {
        val owner = signIn()
        gateway.onSaveWebSite = { _, _ -> ApiResult.Problem(409, "webserver.site_changed", null) }
        repository.save(owner, "nginx", request, ElevationAnswerProvider.Declines)
        assertTrue(journal.pending(owner).isEmpty())
        gateway.onDeleteWebSite = { _, _, _ -> ApiResult.Transport(null) }
        repository.delete(owner, site, ElevationAnswerProvider.Declines)
        gateway.onWebSites = { ApiResult.Success(emptyList()) }
        assertTrue(repository.delete(owner, site, ElevationAnswerProvider.Declines) is ApiResult.Problem)
        assertEquals(1, journal.pending(owner).size)
    }
    @Test fun `accepting facts requires successful readback and session ownership`() = runTest {
        val owner = signIn()
        gateway.onSaveWebSite = { _, _ -> ApiResult.Transport(null) }
        repository.save(owner, "nginx", request, ElevationAnswerProvider.Declines)
        val pending = journal.pending(owner).single()
        gateway.onWebSites = { ApiResult.Transport(null) }
        assertTrue(repository.acceptFacts(owner, pending) is ApiResult.Transport)
        assertEquals(1, journal.pending(owner).size)
        gateway.onWebSites = { ApiResult.Success(listOf(site)) }
        assertTrue(repository.acceptFacts(owner, pending) is ApiResult.Success)
        assertTrue(journal.pending(owner).isEmpty())
        signIn()
        assertTrue(runCatching { repository.sites(owner, "nginx") }.exceptionOrNull() is CancellationException)
    }
    @Test fun `foreign success never clears original pending mutation`() = runTest {
        val owner = signIn()
        gateway.onSaveWebSite = { _, _ -> ApiResult.Success(site.copy(serverId = "other")) }
        assertTrue(repository.save(owner, "nginx", request, ElevationAnswerProvider.Declines) is ApiResult.Transport)
        assertEquals(1, journal.pending(owner).size)
    }
}
