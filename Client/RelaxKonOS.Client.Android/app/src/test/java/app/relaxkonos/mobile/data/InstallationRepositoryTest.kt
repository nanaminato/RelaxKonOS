package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.security.CredentialVault
import app.relaxkonos.mobile.security.FakeVaultCrypto
import app.relaxkonos.mobile.security.InMemoryVaultStorage
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class InstallationRepositoryTest {
    private class JournalStorage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private class IndexStorage : OperationIndexStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val journal = InstallationRequestJournal(JournalStorage())
    private val index = OperationIndex(IndexStorage())
    private val elevation = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()))
    private val repository = InstallationRepository(gateway, session, elevation, index, journal)
    private val id = "00112233-4455-6677-8899-aabbccddeeff"
    private val operation = InstallationOperation(id, InstallationService.Nginx, InstallationKind.Install,
        InstallationState.Running, InstallationStage.Preparing, null, null, 1L, null, null, true)

    @Test fun `explicit original ID identification validates service and kind before resolving pending request`() = runTest {
        val owner = signIn(); val pending = journal.begin(owner, InstallationService.Nginx, InstallationKind.Install, "digest")
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Frp)) }
        assertTrue(repository.identifyOriginal(owner, pending, id) is ApiResult.Transport)
        assertEquals(1, journal.pending(owner).size)
        gateway.onInstallation = { ApiResult.Success(operation) }
        assertTrue(repository.identifyOriginal(owner, pending, id) is ApiResult.Success)
        assertTrue(journal.pending(owner).isEmpty()); assertEquals(id, index.forOwner(owner).single().operationId)
    }
    @Test fun `original ID identification refuses another actor pending marker`() = runTest {
        val owner = signIn(); val pending = journal.begin(owner, InstallationService.Nginx, InstallationKind.Install, "digest")
        signIn("bob"); val other = session.state.value as SessionState.Active
        assertTrue(runCatching { repository.identifyOriginal(other, pending, id) }.isFailure)
    }
    private suspend fun signIn(account: String = "alice", privileged: Boolean = true): SessionState.Active {
        val login = loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.WEB_SERVER), privilegedOperations = privileged)) }
        gateway.onLogin = { _, _, _ -> ApiResult.Success(login.copy(userName = account)) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), account, "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }

    @Test fun `transport ambiguity is retained and explicit replay queries facts with same key`() = runTest {
        val owner = signIn()
        val keys = mutableListOf<String>()
        gateway.onStartInstallation = { _, _, key -> keys += key; ApiResult.Transport("lost response") }
        val intent = repository.prepare(owner, InstallationKind.Install, NginxInstallationRequest(true))
        assertTrue(repository.submit(intent, ElevationAnswerProvider.Declines) is ApiResult.Transport)
        assertEquals(1, keys.size)
        assertTrue(repository.pending(owner).single().attempted)
        var queried = false
        gateway.onActiveInstallation = { queried = true; ApiResult.Success(null) }
        gateway.onStartInstallation = { _, _, key -> assertTrue(queried); keys += key; ApiResult.Success(operation) }
        val restoredIntent = InstallationRepository(gateway, session, elevation, index, journal)
            .prepare(owner, InstallationKind.Install, NginxInstallationRequest(true))
        assertTrue(repository.submit(restoredIntent, ElevationAnswerProvider.Declines) is ApiResult.Success)
        assertEquals(listOf(intent.pending.key, intent.pending.key), keys)
        assertTrue(repository.pending(owner).isEmpty())
        assertEquals(id, index.forOwner(owner).single().operationId)
    }

    @Test fun `elevation retries exact key and clears the supplied credential`() = runTest {
        val owner = signIn()
        val keys = mutableListOf<String>()
        gateway.onStartInstallation = { _, _, key ->
            keys += key
            if (keys.size == 1) ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) else ApiResult.Success(operation)
        }
        gateway.onElevation = { _, _, capability, target, _, _ ->
            assertEquals("nginxInstall", capability); assertEquals("nginx", target)
            ApiResult.Success(ElevationGrant(true, null))
        }
        val password = "secret".toCharArray()
        val intent = repository.prepare(owner, InstallationKind.Install, NginxInstallationRequest(true))
        assertTrue(repository.submit(intent, ElevationAnswerProvider { _, _ -> ElevationAnswer("root", password) }) is ApiResult.Success)
        assertEquals(2, keys.size)
        assertEquals(keys[0], keys[1])
        assertTrue(password.all { it == '\u0000' })
    }

    @Test fun `declined elevation cannot enqueue another request`() = runTest {
        val owner = signIn()
        var calls = 0
        gateway.onStartInstallation = { _, _, _ -> calls++; ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) }
        val intent = repository.prepare(owner, InstallationKind.Install, NginxInstallationRequest(true))
        assertTrue(repository.submit(intent, ElevationAnswerProvider.Declines) is ApiResult.Problem)
        assertEquals(1, calls)
        assertTrue(repository.pending(owner).isEmpty())
        assertTrue(index.forOwner(owner).isEmpty())
    }

    @Test fun `a known recovery ID is queried without replaying a start`() = runTest {
        val owner = signIn()
        val intent = repository.prepare(owner, InstallationKind.Install, NginxInstallationRequest(true))
        journal.update(intent.pending.copy(attempted = true, operationId = id))
        gateway.onInstallation = { queried -> assertEquals(id, queried); ApiResult.Success(operation) }
        assertTrue(repository.submit(intent, ElevationAnswerProvider.Declines) is ApiResult.Success)
        // FakeGateway would throw if start were invoked.
        assertEquals(id, index.forOwner(owner).single().operationId)
    }

    @Test fun `account change discards an old response and cannot pollute new account index`() = runTest {
        val owner = signIn()
        val intent = repository.prepare(owner, InstallationKind.Install, NginxInstallationRequest(true))
        gateway.onStartInstallation = { _, _, _ -> signIn("bob"); ApiResult.Success(operation) }
        val result = runCatching { repository.submit(intent, ElevationAnswerProvider.Declines) }
        assertTrue(result.exceptionOrNull() is CancellationException)
        val bob = session.state.value as SessionState.Active
        assertTrue(index.forOwner(bob).isEmpty())
        assertTrue(repository.pending(bob).isEmpty())
        assertTrue(journal.pending(owner).single().attempted)
    }

    @Test fun `cancel is idempotent and running response is not reported as cancelled`() = runTest {
        val owner = signIn()
        val keys = mutableListOf<String>()
        gateway.onCancelInstallation = { queried, key -> assertEquals(id, queried); keys += key; ApiResult.Success(operation.copy(cancellable = false)) }
        repeat(2) { assertEquals(InstallationState.Running, (repository.cancel(owner, id) as ApiResult.Success).value.state) }
        assertEquals(keys[0], keys[1])
    }

    @Test fun `user mode does not prepare privileged installation intents`() = runTest {
        val owner = signIn(privileged = false)
        assertTrue(runCatching { repository.prepare(owner, InstallationKind.Install, NginxInstallationRequest(true)) }.isFailure)
        assertTrue(repository.pending(owner).isEmpty())
    }
    @Test fun `session change during elevation prompt zeros password without granting new host`() = runTest {
        val owner = signIn()
        gateway.onStartInstallation = { _, _, _ -> ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) }
        val password = "secret".toCharArray()
        val intent = repository.prepare(owner, InstallationKind.Install, NginxInstallationRequest(true))
        val result = runCatching { repository.submit(intent, ElevationAnswerProvider { _, _ ->
            signIn("bob")
            ElevationAnswer("root", password)
        }) }
        assertTrue(result.exceptionOrNull() is CancellationException)
        assertTrue(password.all { it == '\u0000' })
        assertEquals(0, gateway.elevationCount)
    }

    @Test fun `wrong operation ID and inactive discovery cannot enter the index`() = runTest {
        val owner = signIn()
        gateway.onInstallation = { ApiResult.Success(operation.copy(operationId = "10112233-4455-6677-8899-aabbccddeeff")) }
        assertTrue(repository.operation(owner, id) is ApiResult.Transport)
        gateway.onActiveInstallation = { ApiResult.Success(operation.copy(state = InstallationState.Succeeded)) }
        assertTrue(repository.active(owner, InstallationService.Nginx) is ApiResult.Transport)
        assertTrue(index.forOwner(owner).isEmpty())
    }

    @Test fun `explicit recovery reveals only the verified owner record`() = runTest {
        val owner = signIn()
        val bob = owner.copy(userName = "bob")
        for (account in listOf(owner, bob)) {
            index.record(account, OperationDomain.Installation, "Nginx", id)
            index.hide(account, index.forOwner(account).single())
        }
        gateway.onInstallation = { ApiResult.Success(operation) }
        repository.recoverById(owner, id)
        assertEquals(id, index.forOwner(owner).single().operationId)
        assertTrue(index.forOwner(bob).isEmpty())
    }

}
