package app.relaxkonos.mobile.core.net

/**
 * Outcome of one REST call. The distinction between [Problem] and [Transport] is not cosmetic: the
 * design only treats an explicit server rejection as evidence that a stored credential is invalid.
 * A timeout or a 5xx leaves the credential untouched (`RelaxKonOS.Mobile.V1.Design.md` §5.8.2).
 */
sealed interface ApiResult<out T> {
    data class Success<T>(val value: T) : ApiResult<T>

    /** The server answered with a ProblemDetails document. */
    data class Problem(val status: Int, val code: String, val traceId: String?) : ApiResult<Nothing>

    /** The request never produced a verdict: no network, timeout, malformed body, 5xx payload. */
    data class Transport(val detail: String?) : ApiResult<Nothing>
}

/** Stable problem codes this client branches on. Anything else is rendered as a generic error. */
object ProblemCodes {
    const val INVALID_CREDENTIAL = "invalid-credential"

    /**
     * Sign-in throttling (429, `RelaxKonOS.Login.md` §10).
     *
     * Named so a throttled attempt reads as what it is instead of as a generic refusal. It is a verdict
     * about the *rate*, never about the password, so it must stay out of every credential-deletion
     * decision: see [ApiResult.Problem.isCredentialRejection], which excludes it.
     */
    const val LOGIN_RATE_LIMITED = "login-rate-limited"
    const val ACCOUNT_DISABLED = "account-disabled"
    const val ACCOUNT_LOCKED = "account-locked"
    const val ACCOUNT_EXPIRED = "account-expired"
    const val ACCOUNT_RESTRICTION = "account-restriction"
    const val ELEVATION_REQUIRED = "elevation-required"
    const val ELEVATION_PASSWORD_REQUIRED = "elevation-password-required"
    const val ELEVATION_PASSWORD_INVALID = "elevation-password-invalid"
    const val ELEVATION_ACCOUNT_NOT_ADMINISTRATOR = "elevation-account-not-administrator"
    const val PERFORMANCE_NOT_READY = "performance-not-ready"
    const val UNAUTHORIZED = "unauthorized"

    /**
     * The signed-in host identity is not one this server may run ordinary operations as (503,
     * `UserExecutionProblemTypes.IdentityNotEligible`). The identity is the problem, not the
     * deployment: root, a system account below UID 1000, nobody, or an account without a usable home
     * directory. The login response says the same thing through
     * [ExecutionEligibilityReasons], which is why this code must not be read as a rejected credential.
     */
    const val IDENTITY_NOT_ELIGIBLE = "identity-not-eligible"

    /**
     * The identity is fine, but this deployment has no channel to become it (503,
     * `UserExecutionProblemTypes.IdentityNotExecutable`): a missing or mismatched privileged helper, or
     * a User Mode server that runs only as its own account. A fixable deployment fault, and never a
     * rejected credential.
     */
    const val IDENTITY_NOT_EXECUTABLE = "identity-not-executable"

    /**
     * The server holds the file but cannot draw it (415, `FileApiRoutes.Thumbnail`).
     *
     * A normal answer rather than a failure: it means there is no small copy to show — the file is not
     * an image, or it uses a codec the host does not carry — so the client fetches the original as it
     * always did. Nothing here is worth telling the user about, and nothing here is an error.
     */
    const val THUMBNAIL_UNSUPPORTED = "thumbnail-unsupported"

    private const val PREFIX = "https://relaxkonos.app/problems/"

    /**
     * Reads the code from either the `problemCode` extension or the RFC 7807 `type` URI, which is
     * how `RelaxKonOS.Login.md` §5.2 defines the wire contract.
     */
    fun from(type: String?, problemCode: String?): String = when {
        !problemCode.isNullOrBlank() -> problemCode
        type.isNullOrBlank() -> ""
        type.startsWith(PREFIX) -> type.removePrefix(PREFIX)
        else -> type.substringAfterLast('/')
    }

    /**
     * True when the server names a code itself, either through the `problemCode` extension or through a
     * `type` URI in the RelaxKonOS problem namespace. A body that names nothing is a generic error page
     * (a proxy or a framework default), not a contract answer.
     */
    fun namesContractCode(type: String?, problemCode: String?): Boolean =
        !problemCode.isNullOrBlank() || type?.startsWith(PREFIX) == true
}

/**
 * Whether a non-2xx response is read as a server verdict ([ApiResult.Problem]) instead of a transport
 * failure ([ApiResult.Transport]).
 *
 * Every 4xx is a verdict: the server deliberately refused the request. A 5xx is a verdict only when its
 * body names a RelaxKonOS problem code. Two reasons: `RelaxKonOS.Mobile.V1.Design.md` §5.8.2 forbids
 * reading a 5xx as a judgement about the submitted credential, and an unrecognised 5xx body says nothing
 * the UI could put into words. Reporting a named 5xx code as a transport failure is not a safe default —
 * a `503 performance-not-ready` shown as "cannot reach the server" sends the user hunting for a network
 * fault that does not exist.
 */
internal fun readsAsProblem(status: Int, type: String?, problemCode: String?): Boolean =
    status < 500 || ProblemCodes.namesContractCode(type, problemCode)

/**
 * True when this problem means the credential that was just sent must be discarded (design §5.8.2).
 *
 * A status of 500 or above never qualifies, whatever code the body carries: the server did not answer
 * the credential question, so the stored password stays where it is.
 */
fun ApiResult.Problem.isCredentialRejection(): Boolean = status in 400..499 && when (code) {
    ProblemCodes.ELEVATION_PASSWORD_INVALID,
    ProblemCodes.ELEVATION_ACCOUNT_NOT_ADMINISTRATOR,
    ProblemCodes.INVALID_CREDENTIAL,
    -> true

    else -> false
}

/** True when the access token must be refreshed and the call retried exactly once. */
fun ApiResult.Problem.isSessionExpired(): Boolean = status == 401
