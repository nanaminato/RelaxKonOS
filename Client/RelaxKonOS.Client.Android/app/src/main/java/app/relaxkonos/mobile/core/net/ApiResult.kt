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
}

/** True when this problem means the credential that was just sent must be discarded (design §5.8.2). */
fun ApiResult.Problem.isCredentialRejection(): Boolean = when (code) {
    ProblemCodes.ELEVATION_PASSWORD_INVALID,
    ProblemCodes.ELEVATION_ACCOUNT_NOT_ADMINISTRATOR,
    ProblemCodes.INVALID_CREDENTIAL,
    -> true

    else -> false
}

/** True when the access token must be refreshed and the call retried exactly once. */
fun ApiResult.Problem.isSessionExpired(): Boolean = status == 401
