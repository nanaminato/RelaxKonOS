package app.relaxkonos.mobile.data

/** Bounded session-owned metadata only; never stores bytes, credentials or authorization. */
class FileListSnapshots<T>(private val clock: () -> Long = { System.nanoTime() / 1_000_000 }) {
    private data class Snapshot<T>(val value: T, val entries: Int, val written: Long)
    private val snapshots = LinkedHashMap<String, Snapshot<T>>(16, 0.75f, true)
    fun get(path: String): T? {
        val snapshot = snapshots[path] ?: return null
        if (clock() - snapshot.written >= LIFETIME_MS) { snapshots.remove(path); return null }
        return snapshot.value
    }
    fun put(path: String, value: T, entries: Int) {
        if (entries > MAX_ENTRIES) { snapshots.remove(path); return }
        snapshots[path] = Snapshot(value, entries, clock())
        while (snapshots.size > MAX_DIRECTORIES || snapshots.values.sumOf { it.entries } > MAX_ENTRIES)
            snapshots.remove(snapshots.keys.first())
    }
    fun clear() = snapshots.clear()
    companion object { const val LIFETIME_MS = 300_000L; const val MAX_DIRECTORIES = 64; const val MAX_ENTRIES = 50_000 }
}
