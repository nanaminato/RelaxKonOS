package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class CertificateRequestJournalTest {
    @Test fun `corrupt journal fails closed`() = runTest {
        val gateway = FakeGateway(); gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        val session = AuthSession(gateway)
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        val storage = object : InstallationRequestStorage { override fun read() = byteArrayOf(1, 2, 3); override fun write(bytes: ByteArray) = error("Must not overwrite corrupted journal") }
        assertTrue(runCatching { CertificateRequestJournal(storage).pending(session.state.value as SessionState.Active) }.isFailure)
    }
    @Test fun `failed persistence prevents mutation`() = runTest {
        val gateway = FakeGateway()
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.CERTIFICATES), privilegedOperations = true)) }) }
        val session = AuthSession(gateway)
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        val storage = object : InstallationRequestStorage { override fun read(): ByteArray? = null; override fun write(bytes: ByteArray) = error("Disk unavailable") }
        var writes = 0; gateway.onCertificateMutation = { _, _, _, _ -> writes++; ApiResult.Transport(null) }
        val index = OperationIndex(object : OperationIndexStorage { override fun read(): ByteArray? = null; override fun write(bytes: ByteArray) {} })
        val repository = CertificateRepository(gateway, session, index, CertificateRequestJournal(storage), ElevationRepository(gateway, session, app.relaxkonos.mobile.security.CredentialVault(app.relaxkonos.mobile.security.InMemoryVaultStorage(), app.relaxkonos.mobile.security.FakeVaultCrypto()), app.relaxkonos.mobile.data.UsageMemoryStore(app.relaxkonos.mobile.data.InMemoryUsageMemoryStorage())))
        assertTrue(runCatching { repository.submit(session.state.value as SessionState.Active, ElevationAnswerProvider.Declines, CertificateAction.SelfSigned, null, JsonBody()) }.isFailure)
        assertEquals(0, writes)
    }
}
