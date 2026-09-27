package app.relaxkonos.mobile.servercenter

import com.jcraft.jsch.JSchException
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException

/**
 * Debug-only, non-secret trace of the Android SSH handshake.
 *
 * Server-centre verification deliberately presents one short user-facing failure.  On a real
 * device, however, an SSH algorithm mismatch, an unreachable port and a rejected password look
 * identical without a trace.  This object records only the handshake stage, SSH configuration and
 * a classified exception type.  It never receives an endpoint, user name, password, private key,
 * host-key blob, fingerprint, or a raw exception message.
 *
 * The application installs [sink] for debug builds only.  With no sink this is a no-op, which keeps
 * JVM tests and release builds independent of Android Logcat.
 */
internal object SshDiagnostics {
    /** Logcat tag. Filter with `adb logcat -s RelaxKonSsh:D`. */
    const val TAG = "RelaxKonSsh"

    var sink: ((event: String, detail: String?) -> Unit)? = null

    fun trace(event: String, detail: String? = null) {
        sink?.invoke(event, detail)
    }

    fun failure(event: String, error: Throwable) {
        sink?.invoke(event, summary(error))
    }

    private fun summary(error: Throwable): String {
        val classification = when {
            error is ServerCenterHostKeyRejectedException -> "host_key_${error.trust.name.lowercase()}"
            error.findCause<UnknownHostException>() != null -> "name_resolution_failed"
            error.findCause<ConnectException>() != null -> "tcp_connect_failed"
            error.findCause<SocketTimeoutException>() != null -> "connect_timed_out"
            error is JSchException -> jschClassification(error)
            else -> "unexpected_failure"
        }
        return "classification=$classification types=${errorTypeChain(error)} frames=${stackFrames(error)}"
    }

    private fun jschClassification(error: JSchException): String {
        // Inspect fixed library message prefixes only; never copy a message to the log because a
        // server or proxy can put endpoint data into it.
        return when {
            error.message?.startsWith("Auth fail") == true -> "authentication_failed"
            error.message?.startsWith("Auth cancel") == true -> "authentication_cancelled"
            error.message?.startsWith("Algorithm negotiation fail") == true -> "algorithm_negotiation_failed"
            error.message?.startsWith("reject HostKey") == true -> "host_key_rejected"
            else -> "jsch_handshake_failed"
        }
    }

    private fun errorTypeChain(error: Throwable): String = buildList {
        var current: Throwable? = error
        while (current != null && size < 4) {
            add(current.javaClass.simpleName.ifBlank { "UnknownThrowable" })
            current = current.cause
        }
    }.joinToString(">")

    /**
     * Stack locations identify a broken library/provider path without including any runtime value.
     * In particular, do not use [StackTraceElement.toString]: class, method and line are enough for
     * diagnosis and avoid accidentally preserving a future implementation's extra detail.
     */
    private fun stackFrames(error: Throwable): String = error.stackTrace
        .take(STACK_FRAME_LIMIT)
        .joinToString(">") { frame ->
            "${frame.className.substringAfterLast('.')}.${frame.methodName}:${frame.lineNumber}"
        }

    private inline fun <reified T : Throwable> Throwable.findCause(): T? {
        var current: Throwable? = this
        while (current != null) {
            if (current is T) return current
            current = current.cause
        }
        return null
    }

    private const val STACK_FRAME_LIMIT = 6
}
