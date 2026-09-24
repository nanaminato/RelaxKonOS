package app.relaxkonos.mobile.data

import java.io.File

/**
 * One unfinished upload, remembered so a restart, a crash, a network change or a process kill continues
 * from the server's offset instead of re-sending a file that may already be nearly complete.
 *
 * It holds no file content and no credential: only the identifiers needed to ask the server where to
 * continue, plus the URI to read the bytes from again. That is why the file it is stored in is not
 * encrypted — there is nothing in it worth protecting, and saying so here keeps a later reader from
 * assuming the opposite.
 */
data class UploadResumeEntry(
    /** Server / user / workspace / device the session belongs to. An entry is never used against another. */
    val serverKey: String,
    val uploadId: String,
    val targetDirectoryPath: String,
    val fileName: String,
    /** The document this upload read from, kept so the bytes can be re-opened after a restart. */
    val sourceUri: String,
    /** The source signature the session was opened against; a changed document is not resumed. */
    val sourceKey: String,
    val confirmedOffset: Long,
    val totalLength: Long,
    val recordedAtMillis: Long,
)

/**
 * Device-local record of unfinished uploads.
 *
 * Stored as one escaped record per line rather than JSON, for one concrete reason: the Android unit
 * tests run against a stub `org.json` that throws on every call, so a JSON journal could only ever be
 * tested on a device. The format here is small enough to be exact and is exercised by a JVM test.
 */
class UploadResumeJournal(
    private val file: File,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    companion object {
        private const val HEADER = "relaxkonos-upload-resume 1"
        private const val FIELD_COUNT = 9

        /** Matches the server's absolute session lifetime: an older entry could not be resumed anyway. */
        const val MAXIMUM_AGE_MILLIS: Long = 7L * 24 * 60 * 60 * 1000

        /** Mobile devices hold fewer unfinished transfers than a desktop does. */
        const val MAXIMUM_ENTRIES = 16
    }

    /**
     * Finds a resumable entry for this exact document on this exact server.
     *
     * An entry whose signature no longer matches is removed rather than ignored: it can never be resumed
     * again, and leaving it would keep its cache copy alive forever.
     */
    fun find(serverKey: String, sourceKey: String, totalLength: Long): UploadResumeEntry? {
        val entries = read()
        val match = entries.lastOrNull {
            it.serverKey == serverKey && it.sourceKey == sourceKey && it.totalLength == totalLength
        }
        val stale = entries.filter {
            it.sourceKey == sourceKey && it.serverKey == serverKey && it.uploadId != match?.uploadId
        }
        if (stale.isNotEmpty()) {
            write(entries.filterNot { entry -> stale.any { it.uploadId == entry.uploadId } })
        }
        return match
    }

    /** Every unfinished entry for the current server, newest first. */
    fun entries(serverKey: String): List<UploadResumeEntry> =
        read().filter { it.serverKey == serverKey }.sortedByDescending { it.recordedAtMillis }

    fun record(entry: UploadResumeEntry) {
        // Read-modify-write on an immutable list: re-recording the same session replaces its entry rather
        // than appending a second one, which is what keeps "unfinished uploads" from growing duplicates.
        write(read().filterNot { it.uploadId == entry.uploadId } + entry)
    }

    /** Removes one entry and returns it, so its cache copy can be deleted by the same call site. */
    fun remove(uploadId: String): UploadResumeEntry? {
        val entries = read()
        val removed = entries.firstOrNull { it.uploadId == uploadId }
        if (removed != null) write(entries.filterNot { it.uploadId == uploadId })
        return removed
    }

    /**
     * Drops every entry that does not belong to [serverKey] and returns them.
     *
     * Called when the signed-in server / user / workspace / device changes: a session id is only
     * meaningful to the identity that opened it, so an entry from another one can never be resumed. The
     * removed entries are returned rather than forgotten because their cache copies have to go with them.
     */
    fun keepServer(serverKey: String): List<UploadResumeEntry> {
        val entries = read()
        val kept = entries.filter { it.serverKey == serverKey }
        if (kept.size != entries.size) write(kept)
        return entries.filterNot { it.serverKey == serverKey }
    }

    private fun read(): MutableList<UploadResumeEntry> {
        val lines = runCatching { file.readLines() }.getOrNull() ?: return mutableListOf()
        if (lines.firstOrNull()?.trim() != HEADER) return mutableListOf()
        val entries = mutableListOf<UploadResumeEntry>()
        for (line in lines.drop(1)) {
            if (line.isBlank()) continue
            parse(line)?.let { entries.add(it) }
        }
        return trim(entries)
    }

    private fun write(entries: List<UploadResumeEntry>) {
        val trimmed = trim(entries.toMutableList())
        runCatching {
            file.parentFile?.mkdirs()
            file.writeText(buildString {
                append(HEADER).append('\n')
                for (entry in trimmed) {
                    append(encode(entry)).append('\n')
                }
            })
        }
    }

    /**
     * Drops entries that cannot be resumed because they are older than the server would keep them, and
     * the oldest beyond the count limit, so the file cannot grow without bound.
     */
    private fun trim(entries: MutableList<UploadResumeEntry>): MutableList<UploadResumeEntry> {
        val cutoff = clock() - MAXIMUM_AGE_MILLIS
        entries.removeAll { it.recordedAtMillis < cutoff }
        if (entries.size > MAXIMUM_ENTRIES) {
            entries.sortBy { it.recordedAtMillis }
            while (entries.size > MAXIMUM_ENTRIES) entries.removeAt(0)
        }
        return entries
    }

    private fun encode(entry: UploadResumeEntry): String = listOf(
        entry.recordedAtMillis.toString(),
        entry.serverKey,
        entry.uploadId,
        entry.targetDirectoryPath,
        entry.fileName,
        entry.sourceUri,
        entry.sourceKey,
        entry.confirmedOffset.toString(),
        entry.totalLength.toString(),
    ).joinToString("\t") { escape(it) }

    private fun parse(line: String): UploadResumeEntry? = runCatching {
        val fields = line.split('\t')
        if (fields.size != FIELD_COUNT) return null
        val values = fields.map { unescape(it) }
        UploadResumeEntry(
            serverKey = values[1],
            uploadId = values[2],
            targetDirectoryPath = values[3],
            fileName = values[4],
            sourceUri = values[5],
            sourceKey = values[6],
            confirmedOffset = values[7].toLong(),
            totalLength = values[8].toLong(),
            recordedAtMillis = values[0].toLong(),
        ).takeIf { it.uploadId.isNotBlank() }
    }.getOrNull()

    private fun escape(value: String): String = buildString(value.length) {
        for (char in value) {
            when (char) {
                '\\' -> append("\\\\")
                '\t' -> append("\\t")
                '\n' -> append("\\n")
                '\r' -> append("\\r")
                else -> append(char)
            }
        }
    }

    /** Deliberately lenient: an unknown escape is kept verbatim rather than throwing the record away. */
    private fun unescape(value: String): String {
        if ('\\' !in value) return value
        val result = StringBuilder(value.length)
        var index = 0
        while (index < value.length) {
            val char = value[index]
            if (char != '\\' || index == value.length - 1) {
                result.append(char)
                index++
                continue
            }
            when (val next = value[index + 1]) {
                '\\' -> result.append('\\')
                't' -> result.append('\t')
                'n' -> result.append('\n')
                'r' -> result.append('\r')
                else -> result.append('\\').append(next)
            }
            index += 2
        }
        return result.toString()
    }
}
