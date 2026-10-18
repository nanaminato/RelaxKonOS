package app.relaxkonos.mobile.ui.connect

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/** Owns an external scan until its result is consumed or its screen leaves. */
internal class PairingScanState {
    class Ticket internal constructor(internal val originalCode: String)
    private var attached = false
    private var ticket by mutableStateOf<Ticket?>(null)
    val busy get() = ticket != null
    fun attach() { attached = true }
    fun detach() { attached = false; ticket = null }
    fun begin(code: String, submitting: Boolean): Ticket? {
        if (!attached || submitting || busy) return null
        return Ticket(code).also { ticket = it }
    }
    fun owns(expected: Ticket) = attached && ticket === expected
    fun cancel(expected: Ticket) { if (ticket === expected) ticket = null }
    fun finish(expected: Ticket, currentCode: String, submitting: Boolean): Boolean {
        if (!owns(expected)) return false
        ticket = null
        return !submitting && currentCode == expected.originalCode
    }
}
