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
    private val elevation = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()), app.relaxkonos.mobile.data.UsageMemoryStore(app.relaxkonos.mobile.data.InMemoryUsageMemoryStorage()))
    private val repository = InstallationRepository(gateway, session, elevation, index, journal)
    private val id = "00112233-4455-6677-8899-aabbccddeeff"
    private val operation = InstallationOperation(id, InstallationService.Nginx, InstallationKind.Install,
        InstallationState.Running, InstallationStage.Preparing, null, null, 1L, null, null, true)

    @Test fun `managed submission uses frozen plan key across lost response and explicit retry`() = runTest {
        val owner = signIn(capability = ServerCapabilities.PROXY)
        val request = ManagedRuntimeInstallationRequest(true, RUNTIME_PLAN)
        val intent = repository.prepare(owner, InstallationKind.Upgrade, request)
        val keys = mutableListOf<String>()
        gateway.onStartInstallation = { _, _, key -> keys += key; ApiResult.Transport(null) }
        repository.submit(intent, ElevationAnswerProvider { _, _ -> null })
        assertEquals(RUNTIME_KEY, repository.pending(owner).single().key)
        gateway.onActiveInstallation = { ApiResult.Success(null) }
        repository.submit(intent, ElevationAnswerProvider { _, _ -> null })
        assertEquals(listOf(RUNTIME_KEY, RUNTIME_KEY), keys)
        assertTrue(runCatching { repository.prepare(owner, InstallationKind.Upgrade,
            ManagedRuntimeInstallationRequest(true, RUNTIME_PLAN.replace(RUNTIME_KEY, "other-plan-key"))) }.isFailure)
    }

    @Test fun `explicit original ID identification validates service and kind before resolving pending request`() = runTest {
        val owner = signIn(); val pending = journal.begin(owner, InstallationService.Nginx, InstallationKind.Install, "digest")
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Frp)) }
        assertTrue(repository.identifyOriginal(owner, pending, id, InstallationKind.entries.toSet()) is ApiResult.Transport)
        assertEquals(1, journal.pending(owner).size)
        assertTrue(index.forOwner(owner).isEmpty())
        gateway.onInstallation = { ApiResult.Success(operation) }
        assertTrue(repository.identifyOriginal(owner, pending, id, InstallationKind.entries.toSet()) is ApiResult.Success)
        assertTrue(journal.pending(owner).isEmpty()); assertEquals(id, index.forOwner(owner).single().operationId)
    }
    @Test fun `original ID identification refuses another actor pending marker`() = runTest {
        val owner = signIn(); val pending = journal.begin(owner, InstallationService.Nginx, InstallationKind.Install, "digest")
        signIn("bob"); val other = session.state.value as SessionState.Active
        assertTrue(runCatching { repository.identifyOriginal(other, pending, id, InstallationKind.entries.toSet()) }.isFailure)
    }
    @Test fun `polling a verified Mihomo ID resolves its persisted installation marker`() = runTest {
        val owner = signIn(capability = ServerCapabilities.PROXY)
        val pending = journal.begin(owner, InstallationService.Mihomo, InstallationKind.Install, "digest")
        journal.update(pending.copy(attempted = true, operationId = id))
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Mihomo, state = InstallationState.Succeeded)) }
        assertTrue(repository.operation(owner, id) is ApiResult.Success)
        assertTrue(repository.pending(owner).isEmpty())
    }

    @Test fun `successful Mihomo facts do not resolve an installation with unknown ownership`() = runTest {
        val owner = signIn(capability = ServerCapabilities.PROXY)
        val pending = journal.begin(owner, InstallationService.Mihomo, InstallationKind.Install, "digest")
        journal.update(pending.copy(attempted = true))
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Mihomo, state = InstallationState.Succeeded)) }
        assertTrue(repository.operation(owner, id) is ApiResult.Success)
        assertEquals(1, repository.pending(owner).size)
        assertTrue(repository.identifyOriginal(owner, repository.pending(owner).single(), id, InstallationKind.entries.toSet()) is ApiResult.Success)
        assertTrue(repository.pending(owner).isEmpty())
    }
    private suspend fun signIn(account: String = "alice", privileged: Boolean = true, capability: String = ServerCapabilities.WEB_SERVER): SessionState.Active {
        val login = loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(capability), privilegedOperations = privileged)) }
        gateway.onLogin = { _, _, _ -> ApiResult.Success(login.copy(userName = account)) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), account, "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }

    @Test fun `explicit current facts clear unknown installation only when no host task is active`() = runTest {
        val owner = signIn(capability = ServerCapabilities.PROXY)
        val pending = journal.begin(owner, InstallationService.Mihomo, InstallationKind.Install, "digest")
        gateway.onActiveInstallation = { ApiResult.Transport(null) }
        assertTrue(repository.acceptCurrentFacts(owner, pending) is ApiResult.Transport)
        assertEquals(1, repository.pending(owner).size)
        gateway.onActiveInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Mihomo)) }
        assertTrue(repository.acceptCurrentFacts(owner, pending) is ApiResult.Problem)
        assertEquals(1, repository.pending(owner).size)
        gateway.onActiveInstallation = { ApiResult.Success(null) }
        assertTrue(repository.acceptCurrentFacts(owner, pending) is ApiResult.Success)
        assertTrue(repository.pending(owner).isEmpty())
    }

    @Test fun `Docker install grants exact capability and preserves original key with typed confirmed request`() = runTest {
        val owner = signIn(capability = ServerCapabilities.DOCKER); val keys = mutableListOf<String>()
        val current = operation.copy(service = InstallationService.Docker)
        gateway.onStartInstallation = { kind, request, key ->
            assertEquals(InstallationKind.Install, kind); assertEquals(DockerInstallationRequest(true), request); keys += key
            if (keys.size == 1) ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) else ApiResult.Success(current)
        }
        gateway.onElevation = { _, _, capability, target, _, _ ->
            assertEquals("dockerInstall", capability); assertEquals("docker", target); ApiResult.Success(ElevationGrant(true, null))
        }
        val password = "host-admin-secret".toCharArray()
        val intent = repository.prepare(owner, InstallationKind.Install, DockerInstallationRequest(true))
        assertTrue(repository.submit(intent, ElevationAnswerProvider { _, _ -> ElevationAnswer("root", password) }) is ApiResult.Success)
        assertEquals(listOf(intent.pending.key, intent.pending.key), keys); assertTrue(password.all { it == '\u0000' })
        assertTrue(repository.pending(owner).isEmpty()); assertEquals("Docker", index.forOwner(owner).single().resourceId)
        assertTrue(runCatching { repository.prepare(owner, InstallationKind.Upgrade, DockerInstallationRequest(true)) }.isFailure)
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

    @Test fun `scoped recovery rejects another service before index reveal or journal cleanup`() = runTest {
        val owner = signIn()
        val pending = journal.begin(owner, InstallationService.Nginx, InstallationKind.Install, "nginx")
        journal.update(pending.copy(operationId = id))
        index.record(owner, OperationDomain.Installation, InstallationService.Nginx.name, id)
        index.hide(owner, index.forOwner(owner).single())
        gateway.onInstallation = { ApiResult.Success(operation) }
        assertTrue(repository.recoverById(owner, id, InstallationService.Mihomo, InstallationKind.entries.toSet()) is ApiResult.Transport)
        assertTrue(index.forOwner(owner).isEmpty()); assertEquals(id, journal.pending(owner).single().operationId)
        assertTrue(repository.recoverById(owner, id, null, InstallationKind.entries.toSet()) is ApiResult.Success)
        assertTrue(journal.pending(owner).isEmpty()); assertEquals(id, index.forOwner(owner).single().operationId)
    }
    @Test fun `install recovery rejects same service repair before index registration`() = runTest {
        val owner = signIn(capability = ServerCapabilities.DOCKER)
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Docker, kind = InstallationKind.Repair)) }
        assertTrue(repository.recoverById(owner, id, InstallationService.Docker, setOf(InstallationKind.Install)) is ApiResult.Transport)
        assertTrue(index.forOwner(owner).isEmpty())
    }
    @Test fun `install recovery cannot reveal a hidden repair for the same service`() = runTest {
        val owner = signIn(capability = ServerCapabilities.DOCKER)
        index.record(owner, OperationDomain.Installation, InstallationService.Docker.name, id)
        index.hide(owner, index.forOwner(owner).single())
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Docker, kind = InstallationKind.Repair)) }
        repository.recoverById(owner, id, InstallationService.Docker, setOf(InstallationKind.Install))
        assertTrue(index.forOwner(owner).isEmpty())
    }
    @Test fun `install recovery cannot clear a linked same service repair request`() = runTest {
        val owner = signIn(capability = ServerCapabilities.DOCKER)
        val pending = journal.begin(owner, InstallationService.Docker, InstallationKind.Repair, "repair").copy(operationId = id, attempted = true)
        journal.update(pending)
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Docker, kind = InstallationKind.Repair)) }
        repository.recoverById(owner, id, InstallationService.Docker, setOf(InstallationKind.Install))
        assertEquals(listOf(pending), journal.pending(owner))
    }
    @Test fun `explicit full action scope preserves managed runtime and center recovery`() = runTest {
        val owner = signIn(capability = ServerCapabilities.PROXY)
        for (kind in InstallationKind.entries) {
            gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Mihomo, kind = kind)) }
            val scoped = repository.recoverById(owner, id, InstallationService.Mihomo, InstallationKind.entries.toSet())
            assertEquals(kind, (scoped as ApiResult.Success).value.kind)
            val center = repository.recoverById(owner, id, null, InstallationKind.entries.toSet())
            assertEquals(kind, (center as ApiResult.Success).value.kind)
        }
    }
    @Test fun `action scope is frozen before the asynchronous lookup`() = runTest {
        val owner = signIn(capability = ServerCapabilities.DOCKER)
        val allowed = mutableSetOf(InstallationKind.Install)
        gateway.onInstallation = {
            allowed += InstallationKind.Repair
            ApiResult.Success(operation.copy(service = InstallationService.Docker, kind = InstallationKind.Repair))
        }
        assertTrue(repository.recoverById(owner, id, InstallationService.Docker, allowed) is ApiResult.Transport)
        assertTrue(index.forOwner(owner).isEmpty())
    }
    @Test fun `install identification cannot register a same service repair request`() = runTest {
        val owner = signIn(capability = ServerCapabilities.DOCKER)
        val pending = journal.begin(owner, InstallationService.Docker, InstallationKind.Repair, "repair")
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Docker, kind = InstallationKind.Repair)) }
        assertTrue(repository.identifyOriginal(owner, pending, id, setOf(InstallationKind.Install)) is ApiResult.Transport)
        assertTrue(index.forOwner(owner).isEmpty())
    }
    @Test fun `install identification cannot reveal a hidden same service repair`() = runTest {
        val owner = signIn(capability = ServerCapabilities.DOCKER)
        val pending = journal.begin(owner, InstallationService.Docker, InstallationKind.Repair, "repair")
        index.record(owner, OperationDomain.Installation, InstallationService.Docker.name, id)
        index.hide(owner, index.forOwner(owner).single())
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Docker, kind = InstallationKind.Repair)) }
        repository.identifyOriginal(owner, pending, id, setOf(InstallationKind.Install))
        assertTrue(index.forOwner(owner).isEmpty())
    }
    @Test fun `install identification cannot clear an unsupported original action`() = runTest {
        val owner = signIn(capability = ServerCapabilities.DOCKER)
        val pending = journal.begin(owner, InstallationService.Docker, InstallationKind.Repair, "repair")
        var reads = 0
        gateway.onInstallation = { reads++; ApiResult.Success(operation.copy(service = InstallationService.Docker, kind = InstallationKind.Repair)) }
        repository.identifyOriginal(owner, pending, id, setOf(InstallationKind.Install))
        assertEquals(listOf(pending), journal.pending(owner))
        assertEquals(0, reads)
    }
    @Test fun `managed original identification retains all explicitly supported actions`() = runTest {
        val owner = signIn(capability = ServerCapabilities.PROXY)
        for (kind in InstallationKind.entries) {
            val pending = journal.begin(owner, InstallationService.Mihomo, kind, kind.name)
            gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Mihomo, kind = kind)) }
            assertTrue(repository.identifyOriginal(owner, pending, id, InstallationKind.entries.toSet()) is ApiResult.Success)
            assertTrue(journal.pending(owner).isEmpty())
        }
    }
    @Test fun `frp recovery scope rejects standalone rollback and accepts repair`() = runTest {
        val owner = signIn(capability = ServerCapabilities.TUNNELS)
        val kinds = setOf(InstallationKind.Install, InstallationKind.Upgrade, InstallationKind.Repair, InstallationKind.Uninstall)
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Frp, kind = InstallationKind.Rollback)) }
        assertTrue(repository.recoverById(owner, id, InstallationService.Frp, kinds) is ApiResult.Transport)
        assertTrue(index.forOwner(owner).isEmpty())
        gateway.onInstallation = { ApiResult.Success(operation.copy(service = InstallationService.Frp, kind = InstallationKind.Repair)) }
        assertTrue(repository.recoverById(owner, id, InstallationService.Frp, kinds) is ApiResult.Success)
    }
    @Test fun `explicit recovery cannot resolve linked pending request with a different action`() = runTest {
        val owner = signIn()
        val pending = journal.begin(owner, InstallationService.Nginx, InstallationKind.Uninstall, "uninstall")
        journal.update(pending.copy(operationId = id))
        gateway.onInstallation = { ApiResult.Success(operation) }
        repository.recoverById(owner, id, InstallationService.Nginx, setOf(InstallationKind.Install))
        assertEquals(InstallationKind.Uninstall, journal.pending(owner).single().kind)
    }
    @Test fun `explicit recovery reveals only the verified owner record`() = runTest {
        val owner = signIn()
        val bob = owner.copy(userName = "bob")
        for (account in listOf(owner, bob)) {
            index.record(account, OperationDomain.Installation, "Nginx", id)
            index.hide(account, index.forOwner(account).single())
        }
        gateway.onInstallation = { ApiResult.Success(operation) }
        repository.recoverById(owner, id, InstallationService.Nginx, setOf(InstallationKind.Install))
        assertEquals(id, index.forOwner(owner).single().operationId)
        assertTrue(index.forOwner(bob).isEmpty())
    }

}
