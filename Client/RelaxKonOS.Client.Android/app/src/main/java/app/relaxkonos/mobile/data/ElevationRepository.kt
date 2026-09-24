package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.security.CredentialVault
import app.relaxkonos.mobile.security.VaultKind
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * An administrator credential supplied by the user: the account they chose and the password they
 * entered or unlocked.
 *
 * The account travels with the password because they are one answer. Resolving the account separately
 * would misreport the account as soon as the user edits the prefilled name in the dialog.
 *
 * [password] is a mutable sequence and is zeroed by whoever finishes with it.
 */
class ElevationAnswer(val account: String, val password: CharArray)

/** Asks the user for an administrator answer for one exact capability and target. */
fun interface ElevationAnswerProvider {
    suspend fun answer(capability: String, target: String): ElevationAnswer?

    companion object {
        /**
         * The answer for a request nobody made.
         *
         * `null` is the same answer the dialog gives when the user closes it, and the elevation
         * repository turns it into the server's original refusal: nothing is sent, nothing is granted
         * and no stored credential is touched. It exists for calls the user did not ask for a privilege
         * with — an image preview that follows a tap, for instance — so that those can report "this
         * needs authorization" instead of raising a password prompt on their own (design §5.3.8).
         */
        val Declines = ElevationAnswerProvider { _, _ -> null }
    }
}

/** Result of one elevation attempt. */
sealed interface ElevationOutcome {
    data class Granted(val expiresAtMillis: Long?) : ElevationOutcome

    /** The user declined the prompt. Nothing was sent and nothing was discarded. */
    data object Cancelled : ElevationOutcome

    /**
     * The server refused. [credentialDiscarded] reports whether a stored administrator password was
     * dropped as a consequence (design §5.8.2: any rejection discards it).
     */
    data class Rejected(val code: String, val credentialDiscarded: Boolean) : ElevationOutcome

    /** No verdict was produced, so the stored credential is kept. */
    data class Transport(val detail: String?) : ElevationOutcome
}

/** What the elevation dialog must display, and what the user must resolve. */
data class ElevationPrompt(
    val capability: String,
    val target: String,
    val savedAdministratorAccount: String?,
)

/**
 * Bridges "the repository needs an administrator password" and "the user is looking at a dialog".
 *
 * A screen renders [request] as a modal dialog; the dialog answers through [supply] or [cancel].
 * Nothing is ever answered automatically, which keeps "no silent elevation" structural rather than
 * conventional (`RelaxKonOS.Mobile.V1.Design.md` §5.3.8).
 */
class ElevationCoordinator {
    private val requestFlow = MutableStateFlow<ElevationPrompt?>(null)

    val request: StateFlow<ElevationPrompt?> = requestFlow.asStateFlow()

    private var pending: CompletableDeferred<ElevationAnswer?>? = null

    suspend fun request(capability: String, target: String, savedAdministratorAccount: String?): ElevationAnswer? {
        val deferred = CompletableDeferred<ElevationAnswer?>()
        pending = deferred
        requestFlow.value = ElevationPrompt(capability, target, savedAdministratorAccount)
        val answer = deferred.await()
        requestFlow.value = null
        pending = null
        return answer
    }

    fun supply(answer: ElevationAnswer?) {
        pending?.complete(answer)
    }

    fun cancel() {
        pending?.complete(null)
    }
}

/**
 * Host elevation.
 *
 * Client-side grant bookkeeping is advisory only: the server binds every grant to the current access
 * token's `jti`, the capability and the exact target for five minutes. The cache here exists to avoid
 * pointless prompts, and it is dropped the moment the access token changes, because a refreshed token
 * makes all previous grants invalid (design §4.3).
 *
 * The repository never invents a password: [ElevationAnswerProvider] is the elevation dialog, so a
 * credential can only exist because the user answered.
 */
class ElevationRepository(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
    private val vault: CredentialVault,
) {
    /**
     * Cached grant expiry per `capability|target`. Advisory only — the server stays the authority —
     * and bound to [grantsToken], because a refreshed access token carries a new `jti` and every
     * grant issued under the previous one is void (design §4.3).
     */
    private val grants = mutableMapOf<String, Long>()

    /** The access token [grants] belong to. A token change drops every cached entry. */
    private var grantsToken: String? = null

    /** True when this client believes an unexpired grant exists for the exact capability and target. */
    fun hasGrant(capability: String, target: String, nowEpochMillis: Long): Boolean {
        val token = session.accessToken
        if (token == null || token != grantsToken) {
            grants.clear()
            grantsToken = token
            return false
        }
        val expiresAt = grants["$capability|$target"] ?: return false
        return expiresAt > nowEpochMillis
    }

    /** Requests a non-file host capability for one exact target. */
    suspend fun elevateHost(
        capability: String,
        target: String,
        provider: ElevationAnswerProvider,
    ): ElevationOutcome {
        val answer = provider.answer(capability, target) ?: return ElevationOutcome.Cancelled
        val result = try {
            session.authenticated { serverUrl, accessToken ->
                gateway.requestElevation(serverUrl, accessToken, capability, target, answer.password, answer.account)
            }
        } finally {
            answer.password.fill('\u0000')
        }
        return when (result) {
            is ApiResult.Success -> if (result.value.elevated) {
                val expiresAt = result.value.expiresAtMillis ?: (System.currentTimeMillis() + DEFAULT_GRANT_MILLIS)
                rememberGrant(capability, target, expiresAt)
                ElevationOutcome.Granted(result.value.expiresAtMillis)
            } else {
                ElevationOutcome.Rejected(ProblemCodes.ELEVATION_REQUIRED, credentialDiscarded = false)
            }

            is ApiResult.Problem -> ElevationOutcome.Rejected(
                code = result.code,
                credentialDiscarded = discardStoredCredential(answer.account),
            )

            is ApiResult.Transport -> ElevationOutcome.Transport(result.detail)
        }
    }

    /**
     * Runs [call] and, when the server answers `elevation-required`, elevates once and retries the same
     * call exactly once. There is no retry loop: a second `elevation-required` is returned to the
     * caller as-is (design §4.3).
     */
    suspend fun <T> withElevation(
        capability: String,
        target: String,
        provider: ElevationAnswerProvider,
        call: suspend (serverUrl: String, accessToken: String) -> ApiResult<T>,
    ): ApiResult<T> {
        val first = session.authenticated(call)
        if (first !is ApiResult.Problem || first.code != ProblemCodes.ELEVATION_REQUIRED) {
            return first
        }

        return when (val outcome = elevateHost(capability, target, provider)) {
            is ElevationOutcome.Granted -> session.authenticated(call)
            is ElevationOutcome.Rejected -> ApiResult.Problem(403, outcome.code, null)
            is ElevationOutcome.Transport -> ApiResult.Transport(outcome.detail)
            // Declining leaves the operation unfinished; the honest answer is the original refusal.
            ElevationOutcome.Cancelled -> first
        }
    }

    /** File elevation follows the file-specific entry point rather than the host one. */
    suspend fun elevatePath(
        path: String,
        capability: String,
        relatedPaths: List<String>,
        includeDescendants: Boolean,
        provider: ElevationAnswerProvider,
    ): ElevationOutcome {
        val answer = provider.answer(capability, path) ?: return ElevationOutcome.Cancelled
        val result = try {
            session.authenticated { serverUrl, accessToken ->
                gateway.requestFileElevation(
                    serverUrl = serverUrl,
                    accessToken = accessToken,
                    path = path,
                    capability = capability,
                    password = answer.password,
                    relatedPaths = relatedPaths,
                    includeDescendants = includeDescendants,
                    administratorUsername = answer.account,
                )
            }
        } finally {
            answer.password.fill('\u0000')
        }
        return when (result) {
            is ApiResult.Success -> if (result.value.elevated) {
                rememberGrant(capability, path, result.value.expiresAtMillis ?: (System.currentTimeMillis() + DEFAULT_GRANT_MILLIS))
                ElevationOutcome.Granted(result.value.expiresAtMillis)
            } else {
                ElevationOutcome.Rejected(ProblemCodes.ELEVATION_REQUIRED, credentialDiscarded = false)
            }

            is ApiResult.Problem -> ElevationOutcome.Rejected(
                code = result.code,
                credentialDiscarded = discardStoredCredential(answer.account),
            )

            is ApiResult.Transport -> ElevationOutcome.Transport(result.detail)
        }
    }

    /** Wraps one file operation with the single elevation retry. */
    suspend fun <T> withPathElevation(
        path: String,
        capability: String,
        relatedPaths: List<String> = emptyList(),
        includeDescendants: Boolean = false,
        provider: ElevationAnswerProvider,
        call: suspend (serverUrl: String, accessToken: String) -> ApiResult<T>,
    ): ApiResult<T> {
        val first = session.authenticated(call)
        if (first !is ApiResult.Problem || first.code != ProblemCodes.ELEVATION_REQUIRED) {
            return first
        }

        val outcome = elevatePath(path, capability, relatedPaths, includeDescendants, provider)
        return when (outcome) {
            is ElevationOutcome.Granted -> session.authenticated(call)
            is ElevationOutcome.Rejected -> ApiResult.Problem(403, outcome.code, null)
            is ElevationOutcome.Transport -> ApiResult.Transport(outcome.detail)
            ElevationOutcome.Cancelled -> first
        }
    }

    /**
     * Drops one stored administrator password.
     *
     * The rule approved for the mobile client is deliberately blunt: any problem response discards the
     * saved credential, without branching on the error code, so no rejection path can forget to clean
     * up (design §5.8.2). A transport failure or a user cancellation never reaches here.
     */
    private fun discardStoredCredential(account: String): Boolean {
        val serverUrl = session.serverUrl ?: return false
        if (account.isBlank()) {
            return false
        }
        if (vault.record(VaultKind.Elevation, serverUrl, account) == null) {
            return false
        }
        vault.delete(VaultKind.Elevation, serverUrl, account)
        return true
    }

    private fun rememberGrant(capability: String, target: String, expiresAtMillis: Long) {
        val token = session.accessToken ?: return
        if (token != grantsToken) {
            grants.clear()
            grantsToken = token
        }
        grants["$capability|$target"] = expiresAtMillis
    }

    private companion object {
        /** Fallback lifetime when the server omits `expiresAt`; the real grant is still server-side. */
        const val DEFAULT_GRANT_MILLIS = 5 * 60 * 1000L
    }
}
