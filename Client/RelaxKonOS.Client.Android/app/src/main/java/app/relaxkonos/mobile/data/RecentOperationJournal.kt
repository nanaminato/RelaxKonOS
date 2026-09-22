package app.relaxkonos.mobile.data

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Short, in-memory activity journal for the home screen.
 *
 * It intentionally holds only successful user-initiated actions and their visible targets. Passwords,
 * tokens, request bodies and server diagnostic text never enter this object. The journal resets with
 * the process: it is a quick handoff between destinations, not an audit-log substitute.
 */
class RecentOperationJournal(private val capacity: Int = DEFAULT_CAPACITY) {
    private val mutableEntries = MutableStateFlow<List<RecentOperation>>(emptyList())
    val entries = mutableEntries.asStateFlow()

    fun record(kind: RecentOperationKind, target: String, atEpochMillis: Long = System.currentTimeMillis()) {
        val entry = RecentOperation(kind, target, atEpochMillis)
        mutableEntries.value = (listOf(entry) + mutableEntries.value).take(capacity)
    }

    private companion object {
        const val DEFAULT_CAPACITY = 20
    }
}

data class RecentOperation(val kind: RecentOperationKind, val target: String, val atEpochMillis: Long)

enum class RecentOperationKind {
    CreateDirectory,
    Rename,
    Delete,
    Copy,
    Move,
    Upload,
    Download,
    EndProcess,
}
