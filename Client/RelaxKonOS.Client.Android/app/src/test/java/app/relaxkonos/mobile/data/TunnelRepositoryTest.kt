package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class TunnelRepositoryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null; var fail = false
        override fun read() = bytes
        override fun write(bytes: ByteArray) { check(!fail); this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway(); private val session = AuthSession(gateway); private val storage = Storage()
    private val journal = TunnelMutationJournal(storage)
    private val elevations = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()))
    private val repository = TunnelRepository(gateway, session, elevations, journal)
    private val profile = TunnelWire.profile(TUNNEL_PROFILE_JSON)
    private val request = TunnelProfileRequest("edge", "frps.test", 7000, TunnelAuth.Token, TunnelTls.Default, TunnelRuntimeMode.Managed, null, null)
    private suspend fun signIn(manage: Boolean = true): SessionState.Active {
        val login = loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.TUNNELS), privilegedOperations = manage)) }
        gateway.onLogin = { _, _, _ -> ApiResult.Success(login) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    private fun facts() { gateway.onTunnelProfiles = { ApiResult.Success(listOf(profile)) }; gateway.onTunnelDefinitions = { ApiResult.Success(emptyList()) } }
    @Test fun `unknown creation blocks a second write until explicitly accepting fresh facts`() = runTest {
        val owner = signIn(); var writes = 0
        gateway.onSaveTunnelProfile = { _, _ -> writes++; ApiResult.Transport(null) }
        assertTrue(repository.saveProfile(owner, null, request) is ApiResult.Transport)
        assertEquals("tunnel.observation_pending", (repository.saveProfile(owner, null, request) as ApiResult.Problem).code)
        assertEquals(1, writes); facts(); repository.facts(owner)
        assertEquals(1, journal.pending(owner).size)
        assertTrue(repository.acceptFacts(owner, journal.pending(owner).single()) is ApiResult.Success)
        assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `Token is not persisted hashed or inferred successful from configured flag`() = runTest {
        val owner = signIn(); gateway.onTunnelToken = { _, _ -> ApiResult.Transport(null) }
        repository.setToken(owner, profile.id, "private-token-value"); facts()
        assertTrue(repository.facts(owner) is ApiResult.Success)
        val pending = TunnelMutationJournal(storage).pending(owner).single()
        assertEquals(TunnelMutation.SetToken, pending.action)
        assertFalse(storage.bytes!!.decodeToString().contains("private-token-value")); assertEquals(1, journal.pending(owner).size)
    }
    @Test fun `wrong response ID and nonadvancing revision do not clear unknown markers`() = runTest {
        val owner = signIn()
        gateway.onSaveTunnelProfile = { _, _ -> ApiResult.Success(profile.copy(id = TUNNEL_ID)) }
        assertTrue(repository.saveProfile(owner, profile.id, request.copy(expectedRevision = 3)) is ApiResult.Transport)
        assertEquals(1, journal.pending(owner).size); facts(); repository.acceptFacts(owner, journal.pending(owner).single())
        gateway.onSaveTunnelProfile = { _, _ -> ApiResult.Success(profile) }
        assertTrue(repository.saveProfile(owner, profile.id, request.copy(expectedRevision = 3)) is ApiResult.Transport)
        assertEquals(1, journal.pending(owner).size)
    }
    @Test fun `conflict is reported storage failure stops submission and old sessions are rejected`() = runTest {
        val owner = signIn(); gateway.onSaveTunnelProfile = { _, _ -> ApiResult.Problem(409, "tunnel.revision_conflict", null) }
        assertEquals("tunnel.revision_conflict", (repository.saveProfile(owner, profile.id, request) as ApiResult.Problem).code)
        assertTrue(journal.pending(owner).isEmpty()); storage.fail = true; var writes = 0
        gateway.onSaveTunnelProfile = { _, _ -> writes++; ApiResult.Success(profile) }
        assertTrue(runCatching { repository.saveProfile(owner, null, request) }.isFailure); assertEquals(0, writes)
        storage.fail = false; signIn(); assertTrue(runCatching { repository.profiles(owner) }.exceptionOrNull() is CancellationException)
    }
    @Test fun `deletion stops owned profile first and never deletes after lost stop response`() = runTest {
        val owner = signIn(); facts(); var deleted = 0
        gateway.onStopTunnelProfile = { ApiResult.Transport(null) }; gateway.onDeleteTunnelProfile = { deleted++; ApiResult.Success(Unit) }
        assertTrue(repository.deleteProfile(owner, profile, ElevationAnswerProvider.Declines) is ApiResult.Transport)
        assertEquals(0, deleted); repository.acceptFacts(owner, journal.pending(owner).single())
        gateway.onStopTunnelProfile = { ApiResult.Success(TunnelResult(true, TunnelConnectionState.Disconnected, "")) }
        assertTrue(repository.deleteProfile(owner, profile, ElevationAnswerProvider.Declines) is ApiResult.Success); assertEquals(1, deleted)
    }
    @Test fun `readonly session cannot submit Token and wrong download version stays unverified`() = runTest {
        val owner = signIn(false); var writes = 0; gateway.onTunnelToken = { _, _ -> writes++; ApiResult.Success(Unit) }
        assertTrue(runCatching { repository.setToken(owner, profile.id, "token") }.isFailure); assertEquals(0, writes)
        gateway.onTunnelDownload = { ApiResult.Success(TunnelRuntimeDownload("different", "https://example.test/frp")) }
        assertTrue(repository.download(owner, "v0.71.1") is ApiResult.Transport)
    }
}
