package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.security.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class OperationCenterTest {
    private class Storage : InstallationRequestStorage, OperationIndexStorage, BackupRecoveryRequestStorage {
        private var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val index = OperationIndex(Storage())
    private val elevation = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()))
    private val webJournal = WebServerRequestJournal(Storage())
    private val proxyJournal = ProxyRequestJournal(Storage())
    private val smbJournal = SmbMutationJournal(Storage())
    private val dockerResources = DockerResourceJournal(Storage())
    private val dockerJournal = DockerControlJournal(Storage())
    private val center = OperationCenter(session, index, DeploymentRepository(gateway, session, index),
        WebPublishingRepository(gateway, session, elevation, index), DockerRepository(gateway, session, index, testDockerGate(dockerJournal)),
        GitRepositoryClient(gateway, session, index), ScriptTaskRepository(gateway, session, index),
        BackupRecoveryRepository(gateway, session, index, BackupRecoveryRequestJournal(Storage())),
        InstallationRepository(gateway, session, elevation, index, InstallationRequestJournal(Storage())),
        WebServerRepository(gateway, session, elevation, index, webJournal),
        WebSiteRepository(gateway, session, elevation, WebSiteMutationJournal(Storage())),
        CertificateRepository(gateway, session, index, CertificateRequestJournal(Storage())),
        TunnelRepository(gateway, session, elevation, TunnelMutationJournal(Storage())),
        ProxyRepository(gateway, session, index, proxyJournal),
        FirewallRepository(gateway, session, elevation, FirewallMutationJournal(Storage())),
        SmbRepository(gateway, session, elevation, smbJournal),
        DockerControlRepository(gateway, session, dockerJournal, testDockerGate(dockerJournal)),
        DockerResourceRepository(gateway, session, dockerResources, testDockerGate(dockerJournal, dockerResources)))
    private val id = "11111111-1111-1111-1111-111111111111"
    private suspend fun signIn(vararg capabilities: String): SessionState.Active {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let {
            it.copy(server = it.server.copy(capabilities = capabilities.toSet(), privilegedOperations = false))
        }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }

    @Test fun `Docker control unknown records have native recovery without fake remote task IDs`() = runTest {
        val owner = signIn(ServerCapabilities.DOCKER)
        dockerJournal.begin(owner, DockerControlChange(DockerControlKind.EngineRestart))
        val snapshot = center.refresh(owner)
        assertEquals(1, snapshot.pendingDockerControl.size); assertTrue(snapshot.items.isEmpty())
        val other = signIn()
        assertTrue(center.refresh(other).pendingDockerControl.isEmpty()); assertEquals(1, dockerJournal.pending(other).size)
    }

    @Test fun `SMB unknown synchronous writes appear without fake tasks and respect capability removal`() = runTest {
        val owner = signIn(ServerCapabilities.FILE_SERVICES)
        smbJournal.begin(owner, SmbChange(SmbChangeKind.DeleteShare, "opaque"))
        val snapshot = center.refresh(owner)
        assertEquals(1, snapshot.pendingSmb.size); assertTrue(snapshot.items.isEmpty())
        val unavailable = signIn()
        assertTrue(center.refresh(unavailable).pendingSmb.isEmpty())
        assertEquals(1, smbJournal.pending(unavailable).size)
    }

    @Test fun `original proxy task survives restart and lost query without cancellation inference`() = runTest {
        val owner = signIn(ServerCapabilities.PROXY)
        index.record(owner, OperationDomain.Proxy, "lifecycle.start", id)
        gateway.onProxyOperation = { requested -> assertEquals(id, requested); ApiResult.Transport(null) }
        assertEquals(OperationCheck.Unavailable, center.refresh(owner).items.single().check)
        val operation = ProxyOperation(id, "lifecycle.start", ProxyOperationState.Running, "running", "")
        gateway.onProxyOperation = { ApiResult.Success(operation) }
        val verified = center.refresh(owner).items.single()
        assertEquals(OperationCheck.Verified, verified.check)
        assertEquals(operation, verified.proxy)
        assertFalse(verified.cancellable)
        assertTrue(center.cancel(owner, verified) is ApiResult.Transport)
    }

    @Test fun `wrong kind and missing record never become successful and absent capability skips query`() = runTest {
        val owner = signIn(ServerCapabilities.PROXY)
        index.record(owner, OperationDomain.Proxy, "lifecycle.start", id)
        gateway.onProxyOperation = { ApiResult.Success(ProxyOperation(id, "lifecycle.stop", ProxyOperationState.Succeeded, "completed", "")) }
        assertEquals(OperationCheck.Missing, center.refresh(owner).items.single().check)
        gateway.onProxyOperation = { ApiResult.Problem(404, "proxy.operation_not_found", null) }
        assertEquals(OperationCheck.Missing, center.refresh(owner).items.single().check)
        val readOnly = signIn()
        gateway.onProxyOperation = { error("Removed capability must not issue requests") }
        assertEquals(OperationCheck.Unavailable, center.refresh(readOnly).items.single().check)
    }

    @Test fun `unknown Docker resource writes remain markers without fabricated tasks and capability gates recovery`() = runTest {
        val owner = signIn(ServerCapabilities.DOCKER)
        val marker = dockerResources.begin(owner, DockerResourceChange(DockerResourceAction.DeleteVolume, "data"))
        val snapshot = center.refresh(owner)
        assertEquals(listOf(marker), snapshot.pendingDockerResources); assertTrue(snapshot.items.isEmpty())
        val unavailable = signIn(); assertTrue(center.refresh(unavailable).pendingDockerResources.isEmpty())
        assertEquals(listOf(marker), dockerResources.pending(unavailable))
    }

    @Test fun `unanswered web and proxy requests stay distinct without fabricated task IDs`() = runTest {
        val owner = signIn(ServerCapabilities.WEB_SERVER, ServerCapabilities.PROXY)
        val web = webJournal.begin(owner, id, "start").copy(attempted = true)
        webJournal.update(web)
        val proxy = proxyJournal.begin(owner, ProxyAction.Start, null)
        val snapshot = center.refresh(owner)
        assertTrue(snapshot.items.isEmpty())
        assertEquals(listOf(web), snapshot.pendingWebServers)
        assertEquals(listOf(proxy), snapshot.pendingProxy)
        assertEquals(snapshot.pendingWebServers, center.refresh(owner).pendingWebServers)
        assertTrue(center.refresh(signIn()).pendingWebServers.isEmpty())
        assertTrue(center.refresh(session.state.value as SessionState.Active).pendingProxy.isEmpty())
    }
}
