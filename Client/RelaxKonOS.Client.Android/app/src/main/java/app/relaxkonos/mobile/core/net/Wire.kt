package app.relaxkonos.mobile.core.net

import java.io.OutputStream
import java.util.Date
import java.util.GregorianCalendar
import java.util.Locale
import java.util.Calendar
import java.util.TimeZone

/**
 * Minimal JSON object writer used for request bodies.
 *
 * The relay exists for one reason: a password must never be materialised as a `String`. Building the
 * body with `org.json` would require exactly that, so strings come from [java.lang.String] values
 * for everything except credentials, which are written straight from a `CharArray`
 * (`Shell.Design.md` §5.3.2).
 */
class JsonBody {
    private val fields = mutableListOf<Pair<String, (OutputStream) -> Unit>>()

    fun string(name: String, value: String?): JsonBody {
        if (value != null) {
            fields += name to { out -> writeQuoted(out, value) }
        }
        return this
    }

    /** Writes a credential without ever creating an immutable copy of it. */
    fun secret(name: String, value: CharArray?): JsonBody {
        if (value != null) {
            fields += name to { out -> writeQuoted(out, value) }
        }
        return this
    }

    fun nullableSecret(name: String, value: CharArray?): JsonBody {
        if (value == null || value.isEmpty()) {
            fields += name to { out -> out.write("null".toByteArray(Charsets.UTF_8)) }
        } else {
            secret(name, value)
        }
        return this
    }

    fun bool(name: String, value: Boolean): JsonBody {
        fields += name to { out -> out.write(value.toString().toByteArray(Charsets.UTF_8)) }
        return this
    }

    fun int(name: String, value: Int): JsonBody {
        fields += name to { out -> out.write(value.toString().toByteArray(Charsets.UTF_8)) }
        return this
    }

    fun raw(name: String, json: String): JsonBody {
        fields += name to { out -> out.write(json.toByteArray(Charsets.UTF_8)) }
        return this
    }

    fun objectField(name: String, value: JsonBody): JsonBody {
        fields += name to { out -> value.writeTo(out) }
        return this
    }

    fun toByteArray(): ByteArray = java.io.ByteArrayOutputStream().also { writeTo(it) }.toByteArray()

    fun writeTo(target: OutputStream) {
        target.write("{".toByteArray(Charsets.UTF_8))
        fields.forEachIndexed { index, (name, write) ->
            if (index > 0) {
                target.write(",".toByteArray(Charsets.UTF_8))
            }
            writeQuoted(target, name)
            target.write(":".toByteArray(Charsets.UTF_8))
            write(target)
        }
        target.write("}".toByteArray(Charsets.UTF_8))
    }

    private fun writeQuoted(target: OutputStream, value: String) {
        target.write("\"".toByteArray(Charsets.UTF_8))
        writeEscaped(target, value.length) { value[it] }
        target.write("\"".toByteArray(Charsets.UTF_8))
    }

    private fun writeQuoted(target: OutputStream, value: CharArray) {
        target.write("\"".toByteArray(Charsets.UTF_8))
        writeEscaped(target, value.size) { value[it] }
        target.write("\"".toByteArray(Charsets.UTF_8))
    }

    /** RFC 8259 escaping, written one character at a time so no intermediate string is allocated. */
    private inline fun writeEscaped(target: OutputStream, length: Int, next: (Int) -> Char) {
        for (index in 0 until length) {
            val char = next(index)
            when {
                char == '"' -> target.write("\\\"".toByteArray(Charsets.UTF_8))
                char == '\\' -> target.write("\\\\".toByteArray(Charsets.UTF_8))
                char == '\n' -> target.write("\\n".toByteArray(Charsets.UTF_8))
                char == '\r' -> target.write("\\r".toByteArray(Charsets.UTF_8))
                char == '\t' -> target.write("\\t".toByteArray(Charsets.UTF_8))
                // UTF-16 surrogate halves cannot be encoded as UTF-8 independently. JSON
                // escapes preserve the pair for flags/emoji in node names and credentials.
                char < ' ' || char in '\uD800'..'\uDFFF' -> target.write("\\u%04x".format(char.code).toByteArray(Charsets.UTF_8))
                else -> target.write(char.toString().toByteArray(Charsets.UTF_8))
            }
        }
    }
}

/** Current DateTimeOffset wire timestamps on API 23+, with strict proleptic Gregorian validation. */
object IsoInstant {
    private val timestamp = Regex("""(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})""")

    /** Request timestamp formatting always uses wire digits, independent of the phone language. */
    fun fromEpochMillis(millis: Long): String {
        val calendar = GregorianCalendar(TimeZone.getTimeZone("UTC")).apply {
            gregorianChange = Date(Long.MIN_VALUE); timeInMillis = millis
        }
        return String.format(Locale.ROOT, "%04d-%02d-%02dT%02d:%02d:%02dZ",
            calendar.get(Calendar.YEAR), calendar.get(Calendar.MONTH) + 1, calendar.get(Calendar.DAY_OF_MONTH),
            calendar.get(Calendar.HOUR_OF_DAY), calendar.get(Calendar.MINUTE), calendar.get(Calendar.SECOND))
    }

    /** Presentation uses millisecond resolution. Instance identity callers keep the original full string. */
    fun toEpochMillis(value: String?): Long? {
        if (value == null || value.length !in 20..33) return null
        return runCatching { requireEpochMillis(value) }.getOrNull()
    }
    fun requireEpochMillis(value: String): Long {
        require(value.length in 20..33)
        val parts = requireNotNull(timestamp.matchEntire(value)).groupValues
        val year = parts[1].toInt(); require(year in 1..9999)
        val calendar = GregorianCalendar(TimeZone.getTimeZone("UTC")).apply {
            gregorianChange = Date(Long.MIN_VALUE); isLenient = false; clear()
            set(year, parts[2].toInt() - 1, parts[3].toInt(), parts[4].toInt(), parts[5].toInt(), parts[6].toInt())
            set(Calendar.MILLISECOND, parts[7].padEnd(3, '0').take(3).toInt())
        }
        val zone = parts[8]
        val offsetMinutes = if (zone == "Z") 0 else {
            val hours = zone.substring(1, 3).toInt(); val minutes = zone.substring(4, 6).toInt()
            require(hours in 0..14 && minutes in 0..59 && (hours != 14 || minutes == 0))
            (hours * 60 + minutes) * if (zone[0] == '-') -1 else 1
        }
        return calendar.timeInMillis - offsetMinutes * 60_000L
    }
}
