package app.relaxkonos.mobile.servercenter

/**
 * Debug-only, non-secret trace of the Android SSH handshake.
 *
 * Server-centre verification names the cause of a failure in the UI (see [SshFailureReason]) and
 * still keeps this trace: it is the only place that records where in the handshake the attempt
 * stopped, which SSH configuration was used, and the exception type chain with stack locations.
 * It never receives an endpoint, user name, password, private key, host-key blob, fingerprint, or
 * a raw exception message.
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
        // The classification comes from SshFailureRules, so the log and the user-facing sentence can
        // never drift apart. The host-key rejection carries its trust decision, which the reason enum
        // deliberately does not model.
        val classification = when (error) {
            is ServerCenterHostKeyRejectedException -> "host_key_${error.trust.name.lowercase()}"
            else -> SshFailureRules.classify(error).diagnosticName
        }
        return "classification=$classification types=${errorTypeChain(error)} frames=${stackFrames(error)}"
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

    private const val STACK_FRAME_LIMIT = 6
}
