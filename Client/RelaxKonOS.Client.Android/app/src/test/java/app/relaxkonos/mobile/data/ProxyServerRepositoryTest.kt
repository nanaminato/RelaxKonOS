package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.*
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class ProxyServerRepositoryTest {
    private class IndexStorage : OperationIndexStorage {
        var bytes: ByteArray? = null
        override fun read() = bytes
        override fun write(bytes: ByteArray) { this.bytes = bytes.copyOf() }
    }

    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val index = OperationIndex(IndexStorage())
    private val repository = ProxyServerRepository(gateway, session, index)

    private suspend fun signIn(capabilities: Set<String> = setOf(ServerCapabilities.PROXY_SERVER)): SessionState.Active {
        val login = loginSession().let { it.copy(server = it.server.copy(capabilities = capabilities, privilegedOperations = true)) }
        gateway.onLogin = { _, _, _ -> ApiResult.Success(login) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }

    @Test fun `snapshot keeps every fact the workspace renders`() {
        val snapshot = ProxyServerWire.snapshot(SNAPSHOT_JSON)
        assertEquals(7, snapshot.configuration.revision)
        assertEquals(2L, snapshot.publishedRevision)
        assertNull(snapshot.previousRevision)
        assertEquals(ProxyServerProtocol.VlessReality, snapshot.configuration.inbounds.single().protocol)
        assertEquals(443, snapshot.configuration.inbounds.single().publicPort)
        assertEquals(ProxyServerCredentialKind.Uuid, snapshot.capabilities.protocols.single().credentialKind)
        assertEquals(1, snapshot.credentials.single().inboundIds.size)
        assertEquals(1_799_971_200_000L, snapshot.credentials.single().expiresAtMillis)
        assertTrue(snapshot.status.running)
        assertFalse(snapshot.status.applied)
        // A server that cannot report boot state sends null; the field must not collapse into false.
        assertNull(snapshot.status.bootEnabled)
        assertTrue(snapshot.subscriptions.single().revoked)
    }

    @Test fun `publishing records the accepted operation so it stays reachable`() = runTest {
        val owner = signIn()
        val keys = mutableListOf<String>()
        gateway.onProxyServerApply = { key -> keys += key; ApiResult.Success(OPERATION_ID) }
        gateway.onProxyServerOperation = { id ->
            assertEquals(OPERATION_ID, id)
            ApiResult.Success(ProxyWire.operation(OPERATION_JSON))
        }
        val result = repository.publish(owner)
        assertTrue(result is ApiResult.Success)
        assertEquals(ProxyOperationState.Succeeded, (result as ApiResult.Success).value.state)
        assertEquals(1, keys.size)
        // The key is generated per submission and never reused across operations.
        assertNotEquals("", keys.single())
        assertEquals(OPERATION_ID, index.forOwner(owner).single { it.domain == OperationDomain.ProxyServer }.operationId)
    }

    @Test fun `a failed submission records nothing and reports the problem`() = runTest {
        val owner = signIn()
        gateway.onProxyServerApply = { ApiResult.Problem(409, ProxyServerProblemCodes.REVISION_CONFLICT, "trace") }
        val result = repository.publish(owner)
        assertTrue(result is ApiResult.Problem)
        assertEquals(409, (result as ApiResult.Problem).status)
        assertTrue(index.forOwner(owner).none { it.domain == OperationDomain.ProxyServer })
    }

    @Test(expected = IllegalArgumentException::class)
    fun `reading without the capability is refused before any request`() = runTest {
        val owner = signIn(capabilities = setOf(ServerCapabilities.PROXY))
        repository.snapshot(owner)
    }

    @Test fun `export formats carry the reason a format is unavailable`() {
        val options = ProxyServerWire.exportFormats(EXPORT_FORMATS_JSON)
        assertEquals(3, options.size)
        assertTrue(options.first { it.format == ProxyServerExportFormats.SING_BOX }.supported)
        val uri = options.first { it.format == ProxyServerExportFormats.URI }
        assertFalse(uri.supported)
        assertEquals("proxy_server.export_multiple_inbounds", uri.reason)
    }

    @Test fun `saving inbound draft retains revision protocol fields and subscription base`() = runTest {
        val owner = signIn()
        val configuration = ProxyServerWire.snapshot(SNAPSHOT_JSON).configuration
        val request = SaveProxyServerConfiguration(configuration.revision, configuration.inbounds, "https://proxy.example.test")
        gateway.onProxyServerSaveConfiguration = { value, key ->
            val body = org.json.JSONObject(value.body().toByteArray().decodeToString())
            assertEquals(7, body.getLong("expectedRevision")); assertEquals("https://proxy.example.test", body.getString("subscriptionBaseUrl"))
            val inbound = body.getJSONArray("inbounds").getJSONObject(0)
            assertEquals("vlessReality", inbound.getString("protocol")); assertEquals("www.example.com", inbound.getString("handshakeHost"))
            assertFalse(inbound.has("certificateId")); assertTrue(key.isNotBlank())
            ApiResult.Success(OPERATION_ID)
        }
        gateway.onProxyServerOperation = { ApiResult.Success(ProxyWire.operation(OPERATION_JSON.replace("proxy_server.publish", "proxy_server.configuration.save"))) }
        assertTrue(repository.saveConfiguration(owner, request) is ApiResult.Success)
        assertEquals(OPERATION_ID, index.forOwner(owner).single().operationId)
    }
    @Test fun `configuration revision conflict remains a refusal without creating a receipt`() = runTest {
        val owner = signIn()
        gateway.onProxyServerSaveConfiguration = { _, _ -> ApiResult.Problem(409, ProxyServerProblemCodes.REVISION_CONFLICT, null) }
        val result = repository.saveConfiguration(owner, SaveProxyServerConfiguration(1, emptyList(), null))
        assertTrue(result is ApiResult.Problem); assertTrue(index.forOwner(owner).isEmpty())
    }

    private companion object {
        const val OPERATION_ID = "7f0f2b1e-1c2d-4e3f-8a9b-0c1d2e3f4a5b"
        const val OPERATION_JSON = """{"operationId":"$OPERATION_ID","kind":"proxy_server.publish","state":"succeeded",
            "stage":"completed","problemCode":""}"""
        const val INBOUND_ID = "0d1c2b3a-4e5f-4678-9abc-def012345678"
        const val CREDENTIAL_ID = "1a2b3c4d-5e6f-4a8b-9c0d-1e2f3a4b5c6d"
        const val PRINCIPAL_ID = "2b3c4d5e-6f7a-4b8c-9d0e-2f3a4b5c6d7e"
        const val SNAPSHOT_JSON = """
            {"configuration":{"revision":7,"subscriptionBaseUrl":null,"inbounds":[
               {"id":"$INBOUND_ID","name":"edge","protocol":"vlessReality","listenAddress":"0.0.0.0","listenPort":8443,
                "publicHost":"proxy.example.test","publicPort":443,"serverName":"proxy.example.test","certificateId":null,
                "handshakeHost":"www.example.com","handshakePort":443,"webSocketPath":"/proxy","enabled":true}]},
             "principals":[{"id":"$PRINCIPAL_ID","displayName":"Alice","relaxKonOSUserId":null,"credentialCount":1}],
             "credentials":[{"id":"$CREDENTIAL_ID","principalId":"$PRINCIPAL_ID","name":"phone","inboundIds":["$INBOUND_ID"],
                "enabled":true,"expiresAt":"2027-01-15T00:00:00Z","applied":false}],
             "subscriptions":[{"id":"3c4d5e6f-7a8b-4c9d-8e1f-3a4b5c6d7e8f","credentialId":"$CREDENTIAL_ID",
                "formats":["uri"],"createdAt":"2026-10-01T10:00:00Z","expiresAt":null,"revoked":true}],
             "status":{"supported":true,"installed":true,"running":true,"recoveryRequired":false,"version":"1.14.3",
                "configurationHash":"abc","applied":false,"problemCode":"","statisticsSupported":false,"bootEnabled":null},
             "capabilities":{"supported":true,"platform":"linux","architecture":"x64","kernelVersion":"1.14.3",
                "protocols":[{"protocol":"vlessReality","credentialKind":"uuid","udp":false,"requiresCertificate":false,"verified":true}],
                "statistics":false,"logs":false,"serviceControl":true,"bootAutostart":true,"uninstall":true,"subscriptionHosting":true},
             "publishedRevision":2,"previousRevision":null}
        """
        const val EXPORT_FORMATS_JSON = """
            [{"format":"uri","supported":false,"reason":"proxy_server.export_multiple_inbounds"},
             {"format":"sing-box","supported":true,"reason":null},
             {"format":"mihomo","supported":true,"reason":null}]
        """
    }
}
