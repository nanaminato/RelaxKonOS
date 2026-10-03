package app.relaxkonos.mobile.ui.common

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.BuildConfig
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.ExecutionEligibilityReasons
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.data.ReminderKind
import app.relaxkonos.mobile.data.ReminderPreference
import app.relaxkonos.mobile.security.UnlockFailure

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
     * succeeded says so in the success tone instead of borrowing the red of a failure — the shared
     * feedback component presents errors and warnings in a dialog and success inline.
     */
    val tone: StatusTone = StatusTone.Danger,
    /**
     * Set only when the sentence is the consequence of a stable property of this device, in which
     * case the dialog offers "do not remind me again" ([ReminderKind]).
     *
     * A per-attempt outcome — a cancellation, a lockout, a network failure — leaves this `null` on
     * purpose: those are exactly the messages a retry must be able to surface again.
     */
    val reminder: ReminderKind? = null,
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
 * Offers "do not remind me again" for a message whose reason is a device capability rather than an
 * outcome of the attempt, and leaves every other message untouched.
 *
 * The `null` case is the common one and is deliberately a no-op instead of a clear: a shared sentence
 * (an unlock failure, say) is silenceable for one reason and must stay loud for the rest, and the call
 * site is the only place that knows which it is holding.
 */
fun UiMessage.withReminder(kind: ReminderKind?): UiMessage = if (kind == null) this else copy(reminder = kind)

/**
 * The one unlock verdict a screen may let the user answer for good — and only while that answer does
 * not already stand.
 *
 * "No lock screen can unseal a saved password" describes the device, so pressing again cannot read
 * differently: it is the only unlock failure that carries an offer, and it carries the same
 * [ReminderKind.SavedPasswordUnavailable] wherever it is shown (sign-in, server centre, elevation).
 * A lockout, an invalidated key or tampering each leave the user something to do, so they stay loud.
 *
 * A **blocking** dialog cannot use the source-side suppression that a floating notice uses: the
 * elevation dialog shows this sentence so the user knows the authorization did not go through, and
 * skipping it would make pressing Authorize look inert. Hence the answered check lives here, and the
 * offer simply stops being made while the sentence keeps being shown.
 */
internal fun silenceableUnlockVerdict(failure: UnlockFailure, preference: ReminderPreference): ReminderKind? =
    ReminderKind.SavedPasswordUnavailable.takeIf {
        failure == UnlockFailure.Unavailable && !preference.isSilenced(it)
    }

/**
 * Maps a stable problem code to the sentence the user should read.
 *
 * An unknown code degrades to the generic sentence rather than echoing the code: `Shell.Design.md` §8 forbids surfacing raw protocol identifiers, and a code the client does not know is by
 * definition not something it can explain.
 */
fun problemMessage(code: String): UiMessage = UiMessage(
    when (code) {
        ProblemCodes.CONTENT_TOO_LARGE -> R.string.error_content_too_large
        ProblemCodes.INVALID_CREDENTIAL -> R.string.error_invalid_credential
        ProblemCodes.LOGIN_RATE_LIMITED -> R.string.error_login_rate_limited
        // A 503 about the server's own authentication backend, never about the typed password. It gets
        // its own sentence because the generic refusal is actively misleading here: it reads as "the
        // credential was judged and rejected", and the only remedy is on the server, not in the field.
        ProblemCodes.AUTHENTICATION_UNAVAILABLE -> R.string.error_authentication_unavailable
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
        ProblemCodes.IDENTITY_NOT_ELIGIBLE -> R.string.error_identity_not_eligible
        ProblemCodes.IDENTITY_NOT_EXECUTABLE -> R.string.error_identity_not_executable
        else -> R.string.error_generic
    },
)

/**
 * The sentence for an identity the server has already declared unusable, from the reason code the login
 * response carried.
 *
 * The server owns the reason, the client owns the wording — the same split as [problemMessage], and for
 * the same reason: appending the server's English detail to a Chinese screen is not a translation.
 *
 * Every reason falls back to the "not eligible" sentence rather than to nothing. `available = false` is
 * authoritative even for a reason this build has never heard of, and a session that silently loses its
 * file browser is exactly the outcome this notice exists to prevent. A reason the client does not know
 * only costs it the sharper of the two sentences.
 */
fun executionEligibilityMessage(reason: String?): UiMessage = UiMessage(
    when (reason) {
        ExecutionEligibilityReasons.SERVER_ACCOUNT_REQUIRED -> R.string.error_identity_not_executable
        ExecutionEligibilityReasons.WINDOWS_PROFILE_REQUIRED -> R.string.error_windows_profile_required
        ExecutionEligibilityReasons.RESERVED_IDENTITY -> R.string.error_reserved_identity
        ExecutionEligibilityReasons.SYSTEM_ACCOUNT -> R.string.error_system_account
        ExecutionEligibilityReasons.UNVERIFIED_HOME_DIRECTORY -> R.string.error_home_directory_required
        else -> R.string.error_identity_not_eligible
    },
)

/** The message for a failed call, or `null` when the call succeeded. */
fun <T> ApiResult<T>.failureMessage(): UiMessage? = when (this) {
    is ApiResult.Success -> null
    is ApiResult.Problem -> problemMessage(code)
    is ApiResult.Transport -> UiMessage(R.string.error_connectivity)
}
