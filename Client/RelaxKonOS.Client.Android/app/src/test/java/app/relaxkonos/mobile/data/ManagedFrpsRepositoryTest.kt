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

class ManagedFrpsRepositoryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway(); private val session = AuthSession(gateway); private val storage = Storage()
    private val journal = TunnelMutationJournal(storage)
    private val elevations = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()))
    private val repository = TunnelRepository(gateway, session, elevations, journal)
    private val current = ManagedFrpsWire.configuration(FRPS_JSON)
    private val request = ManagedFrpsRequest(true, "127.0.0.1", 7000, listOf(TunnelPortRange(6000, 6010)), null, 443, true, "replacement-private-token".toCharArray(), true,
        "127.0.0.1", 7500, "admin", "replacement-private-password".toCharArray(), 3)
    private suspend fun signIn(account: String = "alice", manage: Boolean = true): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(userName = account, server = it.server.copy(capabilities = setOf(ServerCapabilities.TUNNELS), privilegedOperations = manage)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), account, "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `lost frps save persists no secret and only explicit frps facts resolve marker`() = runTest {
        val owner = signIn(); gateway.onSaveManagedFrps = { ApiResult.Transport(null) }; gateway.onManagedFrps = { ApiResult.Success(current) }
        assertTrue(repository.saveFrps(owner, request) is ApiResult.Transport)
        val pending = TunnelMutationJournal(storage).pending(owner).single()
        assertEquals(TunnelMutation.SaveFrps, pending.action); assertNull(pending.target)
        assertFalse(storage.bytes!!.decodeToString().contains("replacement-private")); assertTrue(request.token!!.all { it == '\u0000' }); assertTrue(request.dashboardPassword!!.all { it == '\u0000' })
        repository.frps(owner); assertEquals(1, journal.pending(owner).size)
        assertTrue(runCatching { repository.acceptFacts(owner, pending) }.isFailure)
        assertTrue(repository.acceptFrpsFacts(owner, pending) is ApiResult.Success); assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `revision conflict is explicit and unchanged success revision remains uncertain`() = runTest {
        val owner = signIn(); gateway.onSaveManagedFrps = { ApiResult.Problem(409, "tunnel.revision_conflict", null) }
        assertTrue(repository.saveFrps(owner, request) is ApiResult.Problem); assertTrue(journal.pending(owner).isEmpty())
        gateway.onSaveManagedFrps = { ApiResult.Success(current) }
        assertTrue(repository.saveFrps(owner, request) is ApiResult.Transport); assertEquals(1, journal.pending(owner).size)
    }
    @Test fun `restart never starts after lost malformed or failed stop result`() = runTest {
        val owner = signIn(); var starts = 0; gateway.onStartManagedFrps = { starts++; ApiResult.Success(TunnelResult(true, TunnelConnectionState.Connected, "")) }
        gateway.onManagedFrps = { ApiResult.Success(current) }; gateway.onStopManagedFrps = { ApiResult.Transport(null) }
        assertTrue(repository.frpsLifecycle(owner, TunnelMutation.RestartFrps, ElevationAnswerProvider.Declines) is ApiResult.Transport); assertEquals(0, starts)
        repository.acceptFrpsFacts(owner, journal.pending(owner).single())
        gateway.onStopManagedFrps = { ApiResult.Success(TunnelResult(true, TunnelConnectionState.Connected, "")) }
        assertTrue(repository.frpsLifecycle(owner, TunnelMutation.RestartFrps, ElevationAnswerProvider.Declines) is ApiResult.Transport); assertEquals(0, starts)
        repository.acceptFrpsFacts(owner, journal.pending(owner).single())
        gateway.onStopManagedFrps = { ApiResult.Success(TunnelResult(false, TunnelConnectionState.Unknown, "tunnel.frps_process_unverified")) }
        val failure = repository.frpsLifecycle(owner, TunnelMutation.RestartFrps, ElevationAnswerProvider.Declines) as ApiResult.Success<TunnelResult>
        assertFalse(failure.value.succeeded); assertEquals(0, starts); assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `restart starts once only after a confirmed disconnected stop`() = runTest {
        val owner = signIn(); val calls = mutableListOf<String>()
        gateway.onStopManagedFrps = { calls += "stop"; ApiResult.Success(TunnelResult(true, TunnelConnectionState.Disconnected, "")) }
        gateway.onStartManagedFrps = { calls += "start"; ApiResult.Success(TunnelResult(true, TunnelConnectionState.Connected, "")) }
        assertTrue(repository.frpsLifecycle(owner, TunnelMutation.RestartFrps, ElevationAnswerProvider.Declines) is ApiResult.Success)
        assertEquals(listOf("stop", "start"), calls); assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `Token read requires matching draft revision and readonly session makes no secret request`() = runTest {
        val owner = signIn(); var reads = 0
        gateway.onManagedFrpsEditing = { reads++; ApiResult.Success(ManagedFrpsEditing(current, "existing-secret".toCharArray())) }
        assertTrue(repository.frpsEditing(owner, 2) is ApiResult.Problem)
        assertEquals("existing-secret", (repository.frpsEditing(owner, 3) as ApiResult.Success<ManagedFrpsEditing>).value.token!!.concatToString())
        val readonly = signIn(manage = false); assertTrue(runCatching { repository.frpsEditing(readonly, 3) }.isFailure); assertEquals(2, reads)
    }
    @Test fun `frps elevation uses exact host target and clears administrator password`() = runTest {
        val owner = signIn(); var starts = 0; val password = "private-admin".toCharArray()
        gateway.onStartManagedFrps = { starts++; if (starts == 1) ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) else ApiResult.Success(TunnelResult(true, TunnelConnectionState.Connected, "")) }
        gateway.onElevation = { _, _, capability, target, _, _ -> assertEquals("frpLifecycle", capability); assertEquals("frps", target); ApiResult.Success(ElevationGrant(true, null)) }
        assertTrue(repository.frpsLifecycle(owner, TunnelMutation.StartFrps, ElevationAnswerProvider { _, _ -> ElevationAnswer("root", password) }) is ApiResult.Success)
        assertEquals(2, starts); assertTrue(password.all { it == '\u0000' })
    }
    @Test fun `session change while reading Token discards the old response`() = runTest {
        val owner = signIn(); val secret = "old-secret".toCharArray(); gateway.onManagedFrpsEditing = { signIn("bob"); ApiResult.Success(ManagedFrpsEditing(current, secret)) }
        assertTrue(runCatching { repository.frpsEditing(owner, 3) }.exceptionOrNull() is CancellationException)
        assertTrue(repository.pending(session.state.value as SessionState.Active).isEmpty()); assertTrue(secret.all { it == '\u0000' })
    }
    @Test fun `start elevation after restart does not repeat the completed stop leg`() = runTest {
        val owner = signIn(); var stops = 0; var starts = 0
        gateway.onStopManagedFrps = { stops++; ApiResult.Success(TunnelResult(true, TunnelConnectionState.Disconnected, "")) }
        gateway.onStartManagedFrps = { starts++; if (starts == 1) ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) else ApiResult.Success(TunnelResult(true, TunnelConnectionState.Connected, "")) }
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Success(ElevationGrant(true, null)) }
        assertTrue(repository.frpsLifecycle(owner, TunnelMutation.RestartFrps, ElevationAnswerProvider { _, _ -> ElevationAnswer("root", "secret".toCharArray()) }) is ApiResult.Success)
        assertEquals(1, stops); assertEquals(2, starts)
    }

}
