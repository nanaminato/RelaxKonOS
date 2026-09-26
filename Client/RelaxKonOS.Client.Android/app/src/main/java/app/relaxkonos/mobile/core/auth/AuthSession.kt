package app.relaxkonos.mobile.core.auth

import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.AuthTokens
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import app.relaxkonos.mobile.core.net.LoginSession
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.core.net.isSessionExpired
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentity
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** What the shell is currently showing. */
sealed interface SessionState {
    data object SignedOut : SessionState

    data object Authenticating : SessionState

    data class Active(
        val serviceId: String,
        val effectiveBaseUrl: String,
        val userName: String,
        val workspaceName: String,
        val capabilities: Set<String>,
        val serverPlatform: String,
        /**
         * Whether this identity may run ordinary operations on the host, as the login response stated
         * it. Screens read it to say so up front; the server still refuses on its own.
         */
        val executionEligibility: ExecutionEligibility,
    ) : SessionState
}

/**
 * Access and refresh tokens held in memory only.
 *
 * The design keeps refresh tokens out of storage on every platform (`RelaxKonOS.Mobile.Design.md`
 * §5.1), which also means a process death simply requires the user to log in again — with the vault
 * that is one fingerprint.
 */
class TokenStore {
    private var tokens: AuthTokens? = null

    val accessToken: String? get() = tokens?.accessToken

    val refreshToken: String? get() = tokens?.refreshToken

    fun update(value: AuthTokens?) {
        tokens = value
    }

    fun clear() {
        tokens = null
    }
}

/**
 * Owns the authentication state machine described in `RelaxKonOS.Mobile.V1.Design.md` §4.2:
 *
 * - a 401 triggers exactly one silent refresh followed by exactly one retry;
 * - a refresh the server explicitly rejects clears the session but never the saved credentials;
 * - a transport failure changes nothing at all.
 */
class AuthSession(
    private val gateway: RelaxKonGateway,
    private val tokenStore: TokenStore = TokenStore(),
) {
    private val stateFlow = MutableStateFlow<SessionState>(SessionState.SignedOut)

    val state: StateFlow<SessionState> = stateFlow.asStateFlow()

    /** Stable identity of the active or most recent session. Used for profiles and credential AAD. */
    var serviceId: String? = null
        private set

    /** Current HTTP address. A managed tunnel may replace this without changing [serviceId]. */
    var effectiveBaseUrl: String? = null
        private set

    private var connectionIdentity: ServerConnectionIdentity? = null

    val accessToken: String? get() = tokenStore.accessToken

    /**
     * Authenticates with the server, runs [afterSuccessfulLogin] while the sign-in UI is still
     * present, then exposes the authenticated shell.
     *
     * Saving a biometric-protected password invokes an Android system prompt. If the active session
     * were published first, Compose would remove the login screen before that prompt could be shown.
     * Keeping this sequencing in the session state machine makes the prompt deterministic instead of
     * relying on a ViewModel that happens to outlive a composable.
     */
    suspend fun login(
        connection: ServerConnectionIdentity,
        identifier: String,
        password: CharArray,
        afterSuccessfulLogin: suspend () -> Unit,
    ): ApiResult<LoginSession> {
        stateFlow.value = SessionState.Authenticating
        return when (val result = gateway.login(connection.effectiveBaseUrl, identifier, password)) {
            is ApiResult.Success -> {
                // Credential/profile work is deliberately performed before publishing the shell so a
                // biometric prompt still has its login-screen host. It is nevertheless auxiliary to
                // the server's successful authentication: a local I/O failure must not leave the
                // state machine stuck in Authenticating forever. The caller can surface that failure
                // after the shell appears, while the authenticated session is always made usable.
                try {
                    afterSuccessfulLogin()
                } catch (cancellation: CancellationException) {
                    // Cancellation means the screen or process is going away, not that local
                    // credential work failed. Do not revive a session while its owner is cancelling.
                    stateFlow.value = SessionState.SignedOut
                    throw cancellation
                } catch (error: Exception) {
                    adopt(connection, result.value)
                    throw error
                }
                adopt(connection, result.value)
                result
            }

            is ApiResult.Problem -> {
                stateFlow.value = SessionState.SignedOut
                result
            }

            is ApiResult.Transport -> {
                // A network failure must not look like a rejected credential.
                stateFlow.value = SessionState.SignedOut
                result
            }
        }
    }

    /** Refreshes the token pair. Returns the renewed tokens, or the reason it failed. */
    suspend fun renew(): ApiResult<AuthTokens> {
        val url = effectiveBaseUrl ?: return ApiResult.Transport("No server has been selected.")
        val refreshToken = tokenStore.refreshToken ?: return ApiResult.Transport("No refresh token is held.")
        return when (val result = gateway.refresh(url, refreshToken)) {
            is ApiResult.Success -> {
                tokenStore.update(result.value)
                result
            }

            is ApiResult.Problem -> {
                clearSession()
                result
            }

            is ApiResult.Transport -> result
        }
    }

    /**
     * Runs [call] with the current access token, applying the single-retry rule.
     *
     * The token is passed in so callers can invalidate their own cached elevation grants when the
     * token actually changes (`jti` changes invalidate host grants: design §4.3).
     */
    suspend fun <T> authenticated(call: suspend (serverUrl: String, accessToken: String) -> ApiResult<T>): ApiResult<T> {
        val url = effectiveBaseUrl ?: return ApiResult.Transport("No server has been selected.")
        val token = tokenStore.accessToken ?: return ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null)

        val first = call(url, token)
        if (first !is ApiResult.Problem || !first.isSessionExpired()) {
            return first
        }
        return when (val renewed = renew()) {
            is ApiResult.Success -> {
                val reboundUrl = effectiveBaseUrl ?: return ApiResult.Transport("No server has been selected.")
                call(reboundUrl, renewed.value.accessToken)
            }
            is ApiResult.Problem -> ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, renewed.traceId)
            is ApiResult.Transport -> renewed
        }
    }

    /**
     * Revokes the refresh token server-side, then drops the in-memory session. The saved connection
     * and its credential stay in the vault so the user can log back in with one fingerprint.
     */
    suspend fun logout() {
        val url = effectiveBaseUrl
        val access = tokenStore.accessToken
        val refresh = tokenStore.refreshToken
        if (url != null && access != null && refresh != null) {
            gateway.logout(url, access, refresh)
        }
        clearSession()
    }

    /** Drops the session without touching the server, e.g. after a rejected refresh. */
    fun clearSession() {
        tokenStore.clear()
        stateFlow.value = SessionState.SignedOut
    }

    /** Updates only the transport address after a verified tunnel rebind. */
    fun updateConnection(connection: ServerConnectionIdentity) {
        val previous = connectionIdentity ?: throw IllegalStateException("No server has been selected.")
        require(ServerConnectionIdentityRules.preservesIdentity(previous, connection)) {
            "A transport rebind must not change the service identity."
        }
        connectionIdentity = connection
        effectiveBaseUrl = connection.effectiveBaseUrl
        val active = stateFlow.value as? SessionState.Active
        if (active != null) stateFlow.value = active.copy(effectiveBaseUrl = connection.effectiveBaseUrl)
    }

    private fun adopt(connection: ServerConnectionIdentity, session: LoginSession) {
        connectionIdentity = connection
        serviceId = connection.serviceId
        effectiveBaseUrl = connection.effectiveBaseUrl
        tokenStore.update(session.tokens)
        stateFlow.value = SessionState.Active(
            serviceId = connection.serviceId,
            effectiveBaseUrl = connection.effectiveBaseUrl,
            userName = session.userName,
            workspaceName = session.workspaceName,
            capabilities = session.server.capabilities,
            serverPlatform = session.server.platform,
            executionEligibility = session.executionEligibility,
        )
    }
}
