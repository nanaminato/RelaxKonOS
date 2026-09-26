package app.relaxkonos.mobile.ui.common

import android.content.Context
import android.text.format.Formatter
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.FileElevationCapabilities
import app.relaxkonos.mobile.security.UnlockFailure
import java.text.DateFormat
import java.util.Date

/** Mirrors `HostElevationCapability.NativeServiceAction`; a host capability, not a file capability. */
const val CAPABILITY_NATIVE_SERVICE_ACTION = "nativeServiceAction"

/**
 * Localised name of an elevation capability.
 *
 * `RelaxKonOS.Mobile.V1.Design.md` §3.4 requires the elevation dialog to name the capability being
 * authorized, while §8 forbids putting the raw enum name on screen. Those two rules together mean the
 * capability must be translated here rather than interpolated.
 */
@Composable
fun capabilityLabel(capability: String): String = stringResource(
    when (capability) {
        FileElevationCapabilities.READ -> R.string.capability_read
        FileElevationCapabilities.WRITE -> R.string.capability_write
        FileElevationCapabilities.CREATE_DIRECTORY -> R.string.capability_create_directory
        FileElevationCapabilities.DELETE -> R.string.capability_delete
        FileElevationCapabilities.RENAME -> R.string.capability_rename
        FileElevationCapabilities.MOVE -> R.string.capability_move
        FileElevationCapabilities.COPY -> R.string.capability_copy
        FileElevationCapabilities.UPLOAD -> R.string.capability_upload
        CAPABILITY_NATIVE_SERVICE_ACTION -> R.string.capability_native_service_action
        else -> R.string.capability_unknown
    },
)

/** Localised text for an unlock failure. The raw platform error is never shown. */
@Composable
fun unlockFailureLabel(failure: UnlockFailure): String = stringResource(unlockFailureRes(failure))

/**
 * The same text from a non-composable context — a coroutine reporting a failed biometric step, for
 * instance. Both entry points share one mapping so the two can never disagree.
 */
fun unlockFailureLabel(context: Context, failure: UnlockFailure): String =
    context.getString(unlockFailureRes(failure))

/** The same failure as a message a screen can hold in its own state. */
fun unlockFailureMessage(failure: UnlockFailure): UiMessage = UiMessage(unlockFailureRes(failure))

private fun unlockFailureRes(failure: UnlockFailure): Int = when (failure) {
    UnlockFailure.LockedOut -> R.string.vault_failure_locked_out
    UnlockFailure.LockedOutPermanently -> R.string.vault_failure_locked_out_permanent
    UnlockFailure.KeyInvalidated -> R.string.vault_failure_key_invalidated
    UnlockFailure.Unavailable -> R.string.vault_failure_unavailable
    UnlockFailure.Tampered -> R.string.vault_failure_tampered
    UnlockFailure.Unknown -> R.string.vault_failure_unknown
}

/** Platform-localised file size, or `null` when the server did not report one. */
@Composable
fun formatSize(bytes: Long?): String? {
    val context = LocalContext.current
    return bytes?.let { Formatter.formatFileSize(context, it) }
}

/** Platform-localised date and time, or `null` when the server did not report one. */
fun formatTimestamp(epochMillis: Long?): String? =
    epochMillis?.let { DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(Date(it)) }
