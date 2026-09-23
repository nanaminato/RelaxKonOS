package app.relaxkonos.mobile.core.auth

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.AuthTokens
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.loginSession
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The authentication state machine of `RelaxKonOS.Mobile.V1.Design.md` §4.2: a 401 refreshes exactly
 * once and retries exactly once, a refresh the server rejects clears the session, and a transport
 * failure changes nothing.
 */
class AuthSessionTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val server = "https://relaxkonos.local:5090"

    private suspend fun signIn(accessToken: String = "access-1", refreshToken: String = "refresh-1") {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession(accessToken, refreshToken)) }
        session.login(server, "nana", "pw".toCharArray()) {}
    }

    @Test
    fun `a successful login activates the session`() = runTest {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession(capabilities = setOf("server.files", "server.metrics"))) }

        val result = session.login(server, "nana", "pw".toCharArray()) {}

        assertTrue(result is ApiResult.Success)
        val state = session.state.value
        assertTrue(state is SessionState.Active)
        state as SessionState.Active
        assertEquals("studio", state.workspaceName)
        assertEquals("nana", state.userName)
        assertEquals("linux", state.serverPlatform)
        assertEquals(setOf("server.files", "server.metrics"), state.capabilities)
        assertEquals(server, session.serverUrl)
        assertEquals("access-1", session.accessToken)
    }

    @Test
    fun `post-login credential work finishes before the shell becomes active`() = runTest {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        var stateDuringCredentialSave: SessionState? = null

        val result = session.login(server, "nana", "pw".toCharArray()) {
            stateDuringCredentialSave = session.state.value
        }

        assertTrue(result is ApiResult.Success)
        assertEquals(SessionState.Authenticating, stateDuringCredentialSave)
        assertTrue(session.state.value is SessionState.Active)
    }

    @Test
    fun `a failed post-login credential step still activates the server session`() = runTest {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }

        val failure = runCatching {
            session.login(server, "nana", "pw".toCharArray()) {
                error("The local profile storage failed.")
            }
        }.exceptionOrNull()

        assertTrue(failure is IllegalStateException)
        assertTrue(session.state.value is SessionState.Active)
        assertEquals("access-1", session.accessToken)
    }

    @Test
    fun `a cancelled post-login step does not revive a session`() = runTest {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }

        val failure = runCatching {
            session.login(server, "nana", "pw".toCharArray()) {
                throw CancellationException("The sign-in screen left composition.")
            }
        }.exceptionOrNull()

        assertTrue(failure is CancellationException)
        assertEquals(SessionState.SignedOut, session.state.value)
        assertNull(session.accessToken)
    }

    @Test
    fun `the server address is normalised before it is stored`() = runTest {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }

        session.login("  https://host:5090/  ", "nana", "pw".toCharArray()) {}

        assertEquals("https://host:5090", session.serverUrl)
    }

    @Test
    fun `a rejected login leaves the session signed out`() = runTest {
        gateway.onLogin = { _, _, _ -> ApiResult.Problem(401, ProblemCodes.INVALID_CREDENTIAL, null) }

        val result = session.login(server, "nana", "pw".toCharArray()) {}

        assertTrue(result is ApiResult.Problem)
        assertEquals(SessionState.SignedOut, session.state.value)
        assertNull(session.accessToken)
    }

    @Test
    fun `a transport failure at login is not treated as a rejected credential`() = runTest {
        gateway.onLogin = { _, _, _ -> ApiResult.Transport("timeout") }

        val result = session.login(server, "nana", "pw".toCharArray()) {}

        assertTrue(result is ApiResult.Transport)
        assertEquals(SessionState.SignedOut, session.state.value)
    }

    /**
     * The only thing that may touch a stored credential after a sign-in attempt is the step that runs
     * when the server accepted it. A rejected sign-in must therefore never reach that step
     * (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §7.3).
     */
    @Test
    fun `a failed sign-in never runs the post-login credential step`() = runTest {
        var credentialStepRan = false
        gateway.onLogin = { _, _, _ -> ApiResult.Problem(401, ProblemCodes.INVALID_CREDENTIAL, null) }

        session.login(server, "nana", "pw".toCharArray()) { credentialStepRan = true }

        assertFalse(credentialStepRan)
    }

    @Test
    fun `a transport failure never runs the post-login credential step`() = runTest {
        var credentialStepRan = false
        gateway.onLogin = { _, _, _ -> ApiResult.Transport("timeout") }

        session.login(server, "nana", "pw".toCharArray()) { credentialStepRan = true }

        assertFalse(credentialStepRan)
    }

    @Test
    fun `a 401 triggers exactly one refresh followed by one retry`() = runTest {
        signIn()
        gateway.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("access-2", "refresh-2", null, null)) }
        val tokensSeen = mutableListOf<String>()

        val result = session.authenticated { _, token ->
            tokensSeen += token
            if (token == "access-1") {
                ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null)
            } else {
                ApiResult.Success("ok")
            }
        }

        assertTrue(result is ApiResult.Success)
        assertEquals(listOf("access-1", "access-2"), tokensSeen)
        assertEquals(1, gateway.refreshCount)
        assertEquals("access-2", session.accessToken)
        assertTrue(session.state.value is SessionState.Active)
    }

    @Test
    fun `a successful call never refreshes`() = runTest {
        signIn()
        gateway.onRefresh = { _, _ -> error("refresh must not be called") }

        val result = session.authenticated { _, _ -> ApiResult.Success("ok") }

        assertTrue(result is ApiResult.Success)
        assertEquals(0, gateway.refreshCount)
    }

    @Test
    fun `a repeated 401 does not cause a second refresh`() = runTest {
        signIn()
        gateway.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("access-2", "refresh-2", null, null)) }
        var calls = 0

        val result = session.authenticated<String> { _, _ ->
            calls++
            ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null)
        }

        assertTrue(result is ApiResult.Problem)
        assertEquals(2, calls)
        assertEquals(1, gateway.refreshCount)
    }

    @Test
    fun `a refresh the server rejects clears the session`() = runTest {
        signIn()
        gateway.onRefresh = { _, _ -> ApiResult.Problem(401, "refresh-token-invalid", null) }

        val result = session.authenticated<String> { _, _ -> ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null) }

        assertTrue(result is ApiResult.Problem)
        assertEquals(SessionState.SignedOut, session.state.value)
        assertNull(session.accessToken)
        // The address survives so the next sign-in can pre-fill it.
        assertEquals(server, session.serverUrl)
    }

    @Test
    fun `a transport failure while refreshing changes nothing`() = runTest {
        signIn()
        gateway.onRefresh = { _, _ -> ApiResult.Transport("timeout") }

        val result = session.authenticated<String> { _, _ -> ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null) }

        assertTrue(result is ApiResult.Transport)
        assertTrue(session.state.value is SessionState.Active)
        assertEquals("access-1", session.accessToken)
    }

    @Test
    fun `a call without a selected server never reaches the gateway`() = runTest {
        gateway.onListDirectory = { _, _, _ -> error("the gateway must not be called") }

        val result = session.authenticated<String> { _, _ -> error("the call must not run") }

        assertTrue(result is ApiResult.Transport)
    }

    @Test
    fun `a call without an access token reports an expired session`() = runTest {
        signIn()
        session.clearSession()

        val result = session.authenticated<String> { _, _ -> error("the call must not run") }

        assertTrue(result is ApiResult.Problem)
        assertEquals(401, (result as ApiResult.Problem).status)
    }

    @Test
    fun `logout revokes the refresh token and clears the session`() = runTest {
        signIn()
        var revoked: Pair<String, String>? = null
        gateway.onLogout = { _, accessToken, refreshToken ->
            revoked = accessToken to refreshToken
            ApiResult.Success(Unit)
        }

        session.logout()

        assertEquals("access-1" to "refresh-1", revoked)
        assertEquals(1, gateway.logoutCount)
        assertEquals(SessionState.SignedOut, session.state.value)
        assertNull(session.accessToken)
    }

    @Test
    fun `logout without a session does not call the gateway`() = runTest {
        gateway.onLogout = { _, _, _ -> error("logout must not be called") }

        session.logout()

        assertEquals(0, gateway.logoutCount)
        assertEquals(SessionState.SignedOut, session.state.value)
    }
}
