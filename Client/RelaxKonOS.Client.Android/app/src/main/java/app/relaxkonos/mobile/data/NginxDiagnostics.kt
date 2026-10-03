package app.relaxkonos.mobile.data

import android.util.Log
import app.relaxkonos.mobile.core.net.ApiResult
import java.util.concurrent.atomic.AtomicLong

/** Bounded process-local trace. Callers supply state metadata, never credentials or response bodies. */
object NginxDiagnostics {
    private val sequence = AtomicLong()
    private val lines = ArrayDeque<String>()
    @Synchronized fun event(message: String) {
        val line = "${sequence.incrementAndGet()} t=${System.nanoTime() / 1_000_000} thread=${Thread.currentThread().name} $message"
        if (lines.size == 200) lines.removeFirst()
        lines.addLast(line)
        runCatching { Log.d("RelaxKonNginx", line) }
    }
    @Synchronized fun snapshot(): String = lines.joinToString("\n")
    fun result(value: ApiResult<*>?): String = when (value) {
        null -> "not-requested"
        is ApiResult.Success -> "success"
        is ApiResult.Problem -> "problem(status=${value.status},code=${value.code})"
        is ApiResult.Transport -> "transport"
    }
}
