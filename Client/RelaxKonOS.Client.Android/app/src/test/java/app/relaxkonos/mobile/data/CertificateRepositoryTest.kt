package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class CertificateRepositoryTest {
    private class Storage : InstallationRequestStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }
    private class IndexStorage : OperationIndexStorage {
        var bytes: ByteArray? = null
        var fail = false
        override fun read() = bytes
        override fun write(bytes: ByteArray) { check(!fail); this.bytes = bytes.copyOf() }
    }
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val storage = Storage()
    private val journal = CertificateRequestJournal(storage)
    private val indexStorage = IndexStorage()
    private val index = OperationIndex(indexStorage)
    private val repository = CertificateRepository(gateway, session, index, journal, ElevationRepository(gateway, session, app.relaxkonos.mobile.security.CredentialVault(app.relaxkonos.mobile.security.InMemoryVaultStorage(), app.relaxkonos.mobile.security.FakeVaultCrypto())))
    private val operation = CertificateWire.operation(CERTIFICATE_OPERATION_JSON)
    private val request = SelfSignedCertificateRequest(listOf("private.example.test"), CertificateKey.EcdsaP256, 365).body()
    private suspend fun signIn(): SessionState.Active {
        val login = loginSession().let { it.copy(server = it.server.copy(capabilities = setOf(ServerCapabilities.CERTIFICATES), privilegedOperations = true)) }
        gateway.onLogin = { _, _, _ -> ApiResult.Success(login) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `self signed elevation retries the original request once`() = runTest {
        val owner = signIn(); val keys = mutableListOf<String>()
        val password = "secret".toCharArray()
        gateway.onCertificateMutation = { _, _, _, key ->
            keys += key
            if (keys.size == 1) ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null)
            else ApiResult.Success(operation.copy(kind = CertificateAction.SelfSigned))
        }
        gateway.onElevation = { _, _, capability, target, _, _ ->
            assertEquals("certificateCreateSelfSigned", capability)
            assertEquals("certificates/self-signed", target)
            ApiResult.Success(ElevationGrant(true, null))
        }
        val result = repository.submit(owner, ElevationAnswerProvider { _, _ -> ElevationAnswer("root", password) }, CertificateAction.SelfSigned, null, request)
        assertTrue(result is ApiResult.Success)
        assertEquals(2, keys.size); assertEquals(keys[0], keys[1])
        assertEquals(1, gateway.elevationCount)
        assertTrue(password.all { it == '\u0000' })
        assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `rejected self signed authorization never retries creation`() = runTest {
        val owner = signIn(); var calls = 0
        gateway.onCertificateMutation = { _, _, _, _ -> calls++; ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) }
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Success(ElevationGrant(false, null)) }
        val result = repository.submit(owner, ElevationAnswerProvider { _, _ -> ElevationAnswer("root", "secret".toCharArray()) }, CertificateAction.SelfSigned, null, request)
        assertTrue(result is ApiResult.Problem)
        assertEquals(1, calls); assertEquals(1, gateway.elevationCount)
    }
    @Test fun `cancelled self signed elevation preserves a retryable draft intent`() = runTest {
        val owner = signIn(); var calls = 0
        gateway.onCertificateMutation = { _, _, _, _ -> calls++; ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) }
        val result = repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.SelfSigned, null, request)
        assertTrue(result is ApiResult.Problem)
        assertEquals(1, calls); assertEquals(0, gateway.elevationCount)
        assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `deployment observation does not settle a lost mutation response`() = runTest {
        val owner = signIn(); val keys = mutableListOf<String>()
        gateway.onCertificateMutation = { _, _, _, key -> keys += key; ApiResult.Transport(null) }
        repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.DeployKestrel, CERTIFICATE_ID, JsonBody())
        gateway.onKestrelDeployment = { ApiResult.Success(CertificateWire.kestrel(KESTREL_JSON)) }
        assertTrue(repository.kestrel(owner, CERTIFICATE_ID) is ApiResult.Success)
        assertEquals(1, journal.pending(owner).size)
        gateway.onCertificateMutation = { _, _, _, key -> keys += key; ApiResult.Success(operation.copy(kind = CertificateAction.DeployKestrel)) }
        repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.DeployKestrel, CERTIFICATE_ID, JsonBody())
        assertEquals(keys[0], keys[1]); assertTrue(journal.pending(owner).isEmpty())
        assertEquals(operation.operationId, index.forOwner(owner).single().operationId)
    }
    @Test fun `live facts for another target remain unverified`() = runTest {
        val owner = signIn()
        gateway.onKestrelDeployment = { ApiResult.Success(CertificateWire.kestrel(KESTREL_JSON).copy(certificateId = "11111111-1111-1111-1111-111111111111")) }
        assertTrue(repository.kestrel(owner, CERTIFICATE_ID) is ApiResult.Transport)
    }
    @Test fun `explicit retry refreshes facts and reuses original key`() = runTest {
        val owner = signIn(); val keys = mutableListOf<String>(); var reads = 0
        gateway.onCertificateMutation = { _, _, _, key -> keys += key; ApiResult.Transport(null) }
        repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.SelfSigned, null, request)
        gateway.onCertificates = { reads++; ApiResult.Success(emptyList()) }
        repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.SelfSigned, null, request)
        assertEquals(1, reads); assertEquals(2, keys.size); assertEquals(keys[0], keys[1]); assertEquals(1, journal.pending(owner).size)
    }
    @Test fun `changed form cannot replace unknown request and plaintext is not persisted`() = runTest {
        val owner = signIn()
        repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.SelfSigned, null, request)
        val result = repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.SelfSigned, null, SelfSignedCertificateRequest(listOf("different.example.test"), CertificateKey.EcdsaP256, 365).body())
        assertEquals("certificate.original_request_pending", (result as ApiResult.Problem).code)
        assertFalse(storage.bytes!!.decodeToString().contains("private.example.test"))
        assertTrue(CertificateRequestJournal(storage).pending(owner).single().attempted)
    }
    @Test fun `wrong action and target in acceptance remain unverified`() = runTest {
        val owner = signIn()
        gateway.onCertificateMutation = { _, _, _, _ -> ApiResult.Success(operation) }
        assertTrue(repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.SelfSigned, null, request) is ApiResult.Transport)
        assertEquals(1, journal.pending(owner).size)
        assertTrue(index.forOwner(owner).isEmpty())
    }
    @Test fun `known operation is durable before index write and lookup finishes recovery`() = runTest {
        val owner = signIn(); indexStorage.fail = true
        val accepted = operation.copy(kind = CertificateAction.SelfSigned)
        gateway.onCertificateMutation = { _, _, _, _ -> ApiResult.Success(accepted) }
        assertTrue(runCatching { repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.SelfSigned, null, request) }.isFailure)
        assertEquals(accepted.operationId, journal.pending(owner).single().operationId)
        indexStorage.fail = false; gateway.onCertificateOperation = { ApiResult.Success(accepted) }
        assertTrue(repository.operation(owner, accepted.operationId) is ApiResult.Success)
        assertTrue(journal.pending(owner).isEmpty()); assertEquals(OperationDomain.Certificate, index.forOwner(owner).single().domain)
    }
    @Test fun `explicit ID recovery validates action and leaves mismatch pending`() = runTest {
        val owner = signIn(); repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.SelfSigned, null, request)
        val pending = journal.pending(owner).single()
        gateway.onCertificateOperation = { ApiResult.Success(operation) }
        assertTrue(repository.recover(owner, operation.operationId, pending) is ApiResult.Transport)
        assertEquals(1, journal.pending(owner).size)
        gateway.onCertificateOperation = { ApiResult.Success(operation.copy(kind = CertificateAction.SelfSigned)) }
        assertTrue(repository.recover(owner, operation.operationId, pending) is ApiResult.Success)
        assertTrue(journal.pending(owner).isEmpty())
    }
    @Test fun `first refusal permits correction while old login and foreign cancellation are rejected`() = runTest {
        val owner = signIn()
        gateway.onCertificateMutation = { _, _, _, _ -> ApiResult.Problem(409, "certificate.domains_invalid", null) }
        repository.submit(owner, ElevationAnswerProvider.Declines, CertificateAction.SelfSigned, null, request); assertTrue(journal.pending(owner).isEmpty())
        gateway.onCertificateCancel = { ApiResult.Success(operation.copy(operationId = "11111111-1111-1111-1111-111111111111")) }
        assertTrue(repository.cancel(owner, operation.operationId) is ApiResult.Transport)
        signIn(); assertTrue(runCatching { repository.list(owner) }.exceptionOrNull() is CancellationException)
    }
}
