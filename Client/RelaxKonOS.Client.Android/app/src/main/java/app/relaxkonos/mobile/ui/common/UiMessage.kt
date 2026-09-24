package app.relaxkonos.mobile.ui.common

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.BuildConfig
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ProblemCodes

/** A localised message: a resource id plus optional format arguments. */
data class UiMessage(
    val resId: Int,
    val args: List<Any> = emptyList(),
    /** Non-sensitive diagnostic context, rendered only in debug builds. */
    val debugDetail: String? = null,
    /**
     * How the message should read.
     *
     * Most of what a screen has to report is a refusal, so the default is danger. An action that
     * succeeded says so in the success tone instead of borrowing the red of a failure — while the
     * banner stays one component, so the two can never drift into different layouts.
     */
    val tone: StatusTone = StatusTone.Danger,
)

@Composable
fun UiMessage.text(): String {
    val primary = stringResource(resId, *args.toTypedArray())
    return if (BuildConfig.DEBUG && !debugDetail.isNullOrBlank()) {
        "$primary\n\n${stringResource(R.string.debug_detail, debugDetail)}"
    } else {
        primary
    }
}

/**
 * Adds bounded, single-line context for a developer build without changing release UI or exposing
 * server response bodies. Callers must pass only protocol metadata or exception summaries.
 */
fun UiMessage.withDebugDetail(detail: String?): UiMessage {
    if (!BuildConfig.DEBUG || detail.isNullOrBlank()) {
        return this
    }
    val normalized = detail.replace(Regex("[\\r\\n\\t]+"), " ").trim().take(240)
    return if (normalized.isEmpty()) this else copy(debugDetail = normalized)
}

/** The sentence shown for a problem the client has no specific mapping for. */
fun genericProblemMessage(): UiMessage = UiMessage(R.string.error_generic)

/**
 * Maps a stable problem code to the sentence the user should read.
 *
 * An unknown code degrades to the generic sentence rather than echoing the code: `RelaxKonOS.Mobile.V1.
 * Design.md` §8 forbids surfacing raw protocol identifiers, and a code the client does not know is by
 * definition not something it can explain.
 */
fun problemMessage(code: String): UiMessage = UiMessage(
    when (code) {
        ProblemCodes.INVALID_CREDENTIAL -> R.string.error_invalid_credential
        ProblemCodes.LOGIN_RATE_LIMITED -> R.string.error_login_rate_limited
        ProblemCodes.ACCOUNT_DISABLED -> R.string.error_account_disabled
        ProblemCodes.ACCOUNT_LOCKED -> R.string.error_account_locked
        ProblemCodes.ACCOUNT_EXPIRED -> R.string.error_account_expired
        ProblemCodes.ACCOUNT_RESTRICTION -> R.string.error_account_restriction
        ProblemCodes.ELEVATION_REQUIRED -> R.string.error_elevation_required
        ProblemCodes.ELEVATION_PASSWORD_REQUIRED -> R.string.error_elevation_password_required
        ProblemCodes.ELEVATION_PASSWORD_INVALID -> R.string.error_elevation_password_invalid
        ProblemCodes.ELEVATION_ACCOUNT_NOT_ADMINISTRATOR -> R.string.error_elevation_not_administrator
        ProblemCodes.PERFORMANCE_NOT_READY -> R.string.error_performance_not_ready
        ProblemCodes.UNAUTHORIZED -> R.string.error_unauthorized
        else -> R.string.error_generic
    },
)

/** The message for a failed call, or `null` when the call succeeded. */
fun <T> ApiResult<T>.failureMessage(): UiMessage? = when (this) {
    is ApiResult.Success -> null
    is ApiResult.Problem -> problemMessage(code)
    is ApiResult.Transport -> UiMessage(R.string.error_connectivity)
}
