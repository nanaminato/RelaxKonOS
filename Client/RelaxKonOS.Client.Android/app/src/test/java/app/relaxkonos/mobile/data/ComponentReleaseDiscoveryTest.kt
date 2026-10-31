package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class ComponentReleaseDiscoveryTest {
    private val release = ComponentRelease("v0.70.0", "linux-arm64", "https://github.com/fatedier/frp/releases/download/v0.70.0/frp_0.70.0_linux_arm64.tar.gz", "frp_0.70.0_linux_arm64.tar.gz")
    private val state = ComponentState("frpc", "host", 2, "managed", "managedArchive", "linux / arm64 / systemd", "v0.69.0", false)
    private suspend fun owner(session: AuthSession, fake: FakeGateway): SessionState.Active {
        fake.onLogin = { _, _, _ -> ApiResult.Success(loginSession().let { it.copy(server = it.server.copy(privilegedOperations = true)) }) }
        session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
        return session.state.value as SessionState.Active
    }
    @Test fun `both requests start and local discovery survives host failure`() = runTest {
        val fake = FakeGateway()
        val session = AuthSession(fake)
        val active = owner(session, fake)
        val serverStarted = CompletableDeferred<Unit>()
        val localStarted = CompletableDeferred<Unit>()
        val gateway = object : RelaxKonGateway by fake {
            override suspend fun componentReleases(serverUrl: String, accessToken: String, componentId: String, rid: String): ApiResult<List<ComponentRelease>> {
                assertEquals("linux-arm64", rid)
                serverStarted.complete(Unit)
                localStarted.await()
                return ApiResult.Transport(null)
            }
        }
        val repository = ComponentRepository(gateway, session, officialReleases = { id, rid ->
            assertEquals("frpc", id); assertEquals("linux-arm64", rid)
            localStarted.complete(Unit); serverStarted.await(); listOf(release)
        }) { _, _, _, _ -> error("Discovery must not install") }
        val result = repository.releases(active, "frpc", state) as ApiResult.Success
        assertEquals(listOf(release), result.value)
        assertTrue(serverStarted.isCompleted && localStarted.isCompleted)
    }
    @Test fun `host discovery survives local failure`() = runTest {
        val fake = FakeGateway(); val session = AuthSession(fake); val active = owner(session, fake)
        val localStarted = CompletableDeferred<Unit>()
        val gateway = object : RelaxKonGateway by fake {
            override suspend fun componentReleases(serverUrl: String, accessToken: String, componentId: String, rid: String): ApiResult<List<ComponentRelease>> {
                localStarted.await(); return ApiResult.Success(listOf(release))
            }
        }
        val repository = ComponentRepository(gateway, session, officialReleases = { _, _ ->
            localStarted.complete(Unit); throw java.io.IOException("offline")
        }) { _, _, _, _ -> error("Discovery must not install") }
        assertEquals(listOf(release), (repository.releases(active, "frpc", state) as ApiResult.Success).value)
    }
    @Test fun `local result cannot outlive its login session`() = runTest {
        val fake = FakeGateway(); val session = AuthSession(fake); val active = owner(session, fake)
        val repository = ComponentRepository(fake, session, officialReleases = { _, _ ->
            session.logout(); listOf(release)
        }) { _, _, _, _ -> error("No install") }
        try {
            repository.releases(active, "frpc", state)
            fail("A stale session must not receive a version list")
        } catch (_: kotlinx.coroutines.CancellationException) { }
    }

    @Test fun `both failures stay distinct from successful empty update filtering`() = runTest {
        val fake = FakeGateway(); val session = AuthSession(fake); val active = owner(session, fake)
        val repository = ComponentRepository(fake, session, officialReleases = { _, _ -> throw java.io.IOException() }) { _, _, _, _ -> error("No install") }
        assertTrue(repository.releases(active, "frpc", state) is ApiResult.Problem)
    }
}
