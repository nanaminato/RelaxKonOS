package app.relaxkonos.mobile.core.net

import java.io.OutputStream
import java.util.Calendar
import java.util.TimeZone

/**
 * Minimal JSON object writer used for request bodies.
 *
 * The relay exists for one reason: a password must never be materialised as a `String`. Building the
 * body with `org.json` would require exactly that, so strings come from [java.lang.String] values
 * for everything except credentials, which are written straight from a `CharArray`
 * (`RelaxKonOS.Mobile.V1.Design.md` §5.3.2).
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
                char < ' ' -> target.write("\\u%04x".format(char.code).toByteArray(Charsets.UTF_8))
                else -> target.write(char.toString().toByteArray(Charsets.UTF_8))
            }
        }
    }
}

/**
 * ISO-8601 parsing without `java.time`.
 *
 * The module keeps `minSdk 23`, and core library desugaring is not part of this slice, so the
 * handful of timestamps the client displays are parsed here. Only the shapes the server actually
 * emits are accepted: `yyyy-MM-ddTHH:mm:ss[.fff][Z|±HH:MM]`. An unparsable value yields `null` and
 * the UI simply omits the timestamp instead of showing a wrong one.
 */
object IsoInstant {
    fun toEpochMillis(value: String?): Long? {
        if (value.isNullOrBlank()) {
            return null
        }
        return try {
            // The shape is `yyyy-MM-ddTHH:mm:ss`, so the separator sits at index 10. Anything else is
            // rejected rather than guessed at.
            val separator = value.indexOf('T')
            if (separator != 10) {
                return null
            }
            val year = value.substring(0, 4).toInt()
            val month = value.substring(5, 7).toInt()
            val day = value.substring(8, 10).toInt()
            var rest = value.substring(11)

            var offsetSeconds = 0
            when {
                rest.endsWith("Z") -> rest = rest.dropLast(1)
                else -> {
                    val signIndex = rest.indexOfLast { it == '+' || it == '-' }
                    if (signIndex > 0) {
                        val sign = if (rest[signIndex] == '-') -1 else 1
                        val offset = rest.substring(signIndex + 1)
                        offsetSeconds = sign * (offset.substring(0, 2).toInt() * 3600 + offset.substring(3, 5).toInt() * 60)
                        rest = rest.substring(0, signIndex)
                    }
                }
            }

            val fractionIndex = rest.indexOf('.')
            val millis = if (fractionIndex >= 0) {
                rest.substring(fractionIndex + 1).padEnd(3, '0').take(3).toInt()
            } else {
                0
            }
            val time = if (fractionIndex >= 0) rest.substring(0, fractionIndex) else rest

            Calendar.getInstance(TimeZone.getTimeZone("UTC")).apply {
                clear()
                set(year, month - 1, day, time.substring(0, 2).toInt(), time.substring(3, 5).toInt(), time.substring(6, 8).toInt())
                set(Calendar.MILLISECOND, millis)
            }.timeInMillis - offsetSeconds * 1000L
        } catch (_: Exception) {
            null
        }
    }
}
