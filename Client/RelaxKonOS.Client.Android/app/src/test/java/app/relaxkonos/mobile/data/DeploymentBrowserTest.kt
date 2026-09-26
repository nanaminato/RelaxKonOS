package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class DeploymentBrowserTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val repository = DeploymentRepository(gateway, session)
    private val capabilities = setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS, ServerCapabilities.DOCKER)
    private val runtime = DeploymentRuntime(true, "", "28", "linux", "amd64")
    private val app = DeploymentApplication("id-1", "first", "Image", "Web", "Running", "Unknown", "Http",
        null, null, 80, null, "127.0.0.1", null, null)

    private suspend fun signIn(user: String = "nana", caps: Set<String> = capabilities, url: String = "https://server.local") {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession(userName = user, capabilities = caps)) }
        session.login(ServerConnectionIdentityRules.direct(url), user, charArrayOf('p')) {}
    }

    private fun reads() {
        gateway.onDeploymentRuntime = { _, _ -> ApiResult.Success(runtime) }
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Success(listOf(app)) }
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Success(DeploymentSnapshot(app, emptyList(), emptyList(), null)) }
    }

    @Test fun `absent deployment capability performs no network reads`() = runTest {
        signIn(caps = emptySet())
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        assertNull(browser.state.value.applications)
        assertFalse(browser.state.value.loading)
    }

    @Test fun `missing Docker capability does not prevent reading deployment records`() = runTest {
        signIn(caps = setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS))
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Success(listOf(app)) }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        assertNull(browser.state.value.runtime)
        assertEquals(listOf(app), (browser.state.value.applications as ApiResult.Success).value)
    }

    @Test fun `engine failure does not hide drift-aware application records`() = runTest {
        signIn(); reads()
        gateway.onDeploymentRuntime = { _, _ -> ApiResult.Success(runtime.copy(isAvailable = false, problemCode = "docker.unavailable")) }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        assertEquals("Unknown", (browser.state.value.applications as ApiResult.Success).value.single().actualState)
        assertFalse((browser.state.value.runtime as ApiResult.Success).value.isAvailable)
    }

    @Test fun `failed refresh discards previously successful runtime and detail`() = runTest {
        signIn(); reads()
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        browser.select(app.id); runCurrent()
        gateway.onDeploymentRuntime = { _, _ -> ApiResult.Transport(null) }
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Transport(null) }
        gateway.onDeploymentSnapshot = { _, _, _ -> ApiResult.Transport(null) }
        browser.refresh(); runCurrent()
        assertTrue(browser.state.value.runtime is ApiResult.Transport)
        assertTrue(browser.state.value.applications is ApiResult.Transport)
        assertTrue(browser.state.value.detail is ApiResult.Transport)
    }

    @Test fun `permission rejection remains distinct from transport failure without elevation`() = runTest {
        signIn(); reads()
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Problem(403, "", null) }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        assertEquals(403, (browser.state.value.applications as ApiResult.Problem).status)
        assertEquals(0, gateway.elevationCount)
    }

    @Test fun `signout clears selected application and all loaded data`() = runTest {
        signIn(); reads()
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent(); browser.select(app.id); runCurrent()
        session.clearSession(); runCurrent()
        assertEquals(DeploymentBrowserState(), browser.state.value)
    }

    @Test fun `late old account response cannot populate the new account`() = runTest {
        signIn(); reads()
        val gate = CompletableDeferred<Unit>()
        gateway.onDeploymentApplications = { _, _ ->
            withContext(NonCancellable) { gate.await() }
            ApiResult.Success(listOf(app))
        }
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        gateway.onDeploymentApplications = { _, _ -> ApiResult.Success(emptyList()) }
        signIn(user = "other"); runCurrent()
        gate.complete(Unit); runCurrent()
        assertEquals("other", browser.state.value.owner!!.userName)
        assertTrue((browser.state.value.applications as ApiResult.Success).value.isEmpty())
        assertNull(browser.state.value.selectedId)
    }

    @Test fun `late detail response cannot replace newly selected application`() = runTest {
        signIn(); reads()
        val browser = DeploymentBrowser(repository, session, backgroundScope)
        runCurrent()
        val gate = CompletableDeferred<Unit>()
        gateway.onDeploymentSnapshot = { _, _, id ->
            if (id == "first") withContext(NonCancellable) { gate.await() }
            ApiResult.Success(DeploymentSnapshot(app.copy(id = id), emptyList(), emptyList(), null))
        }
        browser.select("first"); runCurrent()
        browser.select("second"); runCurrent()
        gate.complete(Unit); runCurrent()
        assertEquals("second", (browser.state.value.detail as ApiResult.Success).value.application.id)
        assertFalse(browser.state.value.detailLoading)
    }

    @Test fun `expired access token retries read with refreshed token once`() = runTest {
        signIn()
        val tokens = mutableListOf<String>()
        gateway.onDeploymentApplications = { _, token ->
            tokens += token
            if (tokens.size == 1) ApiResult.Problem(401, "", null) else ApiResult.Success(emptyList())
        }
        gateway.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("new-token", "new-refresh", null, null)) }
        assertTrue(repository.applications(session.state.value as SessionState.Active) is ApiResult.Success)
        assertEquals(listOf("access-1", "new-token"), tokens)
        assertEquals(1, gateway.refreshCount)
    }

    @Test fun `previous server owner cannot initiate a read on the new server`() = runTest {
        signIn()
        val owner = session.state.value as SessionState.Active
        signIn(url = "https://another.local")
        try {
            repository.applications(owner)
            fail("Expected cancellation before any read")
        } catch (_: CancellationException) { }
    }

    @Test fun `overlapping list and runtime reads rotate the expired token only once`() = runTest {
        signIn()
        val owner = session.state.value as SessionState.Active
        val gate = CompletableDeferred<Unit>()
        val tokens = mutableListOf<String>()
        gateway.onDeploymentApplications = { _, token ->
            tokens += token
            if (token == "access-1") {
                gate.await()
                ApiResult.Problem(401, "", null)
            } else ApiResult.Success(emptyList())
        }
        gateway.onDeploymentRuntime = { _, token ->
            tokens += token
            ApiResult.Success(runtime)
        }
        gateway.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("new-token", "new-refresh", null, null)) }
        val applications = async { repository.applications(owner) }
        val engine = async { repository.runtime(owner) }
        runCurrent()
        assertEquals(listOf("access-1"), tokens)
        gate.complete(Unit)
        assertTrue(applications.await() is ApiResult.Success)
        assertTrue(engine.await() is ApiResult.Success)
        assertEquals(listOf("access-1", "new-token", "new-token"), tokens)
        assertEquals(1, gateway.refreshCount)
    }
}
