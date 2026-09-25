package app.relaxkonos.mobile.servercenter

/**
 * Small strict JSON reader used by the deployment wire contract.
 *
 * Android's `org.json` is a throwing stub in local JVM tests, while deployment receipts must be
 * validated identically before UI code sees them. This reader deliberately supports only JSON and
 * rejects duplicate object keys, trailing data and excessive nesting.
 */
internal sealed interface ServerCenterJsonValue {
    data class ObjectValue(val fields: Map<String, ServerCenterJsonValue>) : ServerCenterJsonValue
    data class ArrayValue(val values: List<ServerCenterJsonValue>) : ServerCenterJsonValue
    data class StringValue(val value: String) : ServerCenterJsonValue
    data class NumberValue(val token: String) : ServerCenterJsonValue
    data class BooleanValue(val value: Boolean) : ServerCenterJsonValue
    data object NullValue : ServerCenterJsonValue
}

internal object ServerCenterJson {
    fun parse(text: String, maximumChars: Int): ServerCenterJsonValue {
        require(text.length in 1..maximumChars) { "JSON input is empty or too large." }
        return Parser(text).parse()
    }

    fun quote(value: String): String = buildString(value.length + 2) {
        append('"')
        for (character in value) {
            when (character) {
                '"' -> append("\\\"")
                '\\' -> append("\\\\")
                '\b' -> append("\\b")
                '\u000C' -> append("\\f")
                '\n' -> append("\\n")
                '\r' -> append("\\r")
                '\t' -> append("\\t")
                else -> if (character.code < 0x20) {
                    append("\\u")
                    append(character.code.toString(16).padStart(4, '0'))
                } else append(character)
            }
        }
        append('"')
    }

    private class Parser(private val text: String) {
        private var index = 0

        fun parse(): ServerCenterJsonValue {
            skipWhitespace()
            val value = readValue(0)
            skipWhitespace()
            require(index == text.length) { "JSON contains trailing data." }
            return value
        }

        private fun readValue(depth: Int): ServerCenterJsonValue {
            require(depth <= MAXIMUM_DEPTH) { "JSON nesting is too deep." }
            require(index < text.length) { "Unexpected end of JSON." }
            return when (text[index]) {
                '{' -> readObject(depth + 1)
                '[' -> readArray(depth + 1)
                '"' -> ServerCenterJsonValue.StringValue(readString())
                't' -> readLiteral("true", ServerCenterJsonValue.BooleanValue(true))
                'f' -> readLiteral("false", ServerCenterJsonValue.BooleanValue(false))
                'n' -> readLiteral("null", ServerCenterJsonValue.NullValue)
                '-', in '0'..'9' -> readNumber()
                else -> throw IllegalArgumentException("Invalid JSON token.")
            }
        }

        private fun readObject(depth: Int): ServerCenterJsonValue.ObjectValue {
            index++
            skipWhitespace()
            val fields = linkedMapOf<String, ServerCenterJsonValue>()
            if (consume('}')) return ServerCenterJsonValue.ObjectValue(fields)
            while (true) {
                require(index < text.length && text[index] == '"') { "Object key must be a string." }
                val key = readString()
                require(!fields.containsKey(key)) { "Object contains a duplicate key." }
                skipWhitespace()
                require(consume(':')) { "Object key is missing a value." }
                skipWhitespace()
                fields[key] = readValue(depth)
                skipWhitespace()
                if (consume('}')) break
                require(consume(',')) { "Object entries must be comma separated." }
                skipWhitespace()
            }
            return ServerCenterJsonValue.ObjectValue(fields)
        }

        private fun readArray(depth: Int): ServerCenterJsonValue.ArrayValue {
            index++
            skipWhitespace()
            val values = mutableListOf<ServerCenterJsonValue>()
            if (consume(']')) return ServerCenterJsonValue.ArrayValue(values)
            while (true) {
                values += readValue(depth)
                skipWhitespace()
                if (consume(']')) break
                require(consume(',')) { "Array entries must be comma separated." }
                skipWhitespace()
            }
            return ServerCenterJsonValue.ArrayValue(values)
        }

        private fun readString(): String {
            require(consume('"')) { "String opening quote is missing." }
            val result = StringBuilder()
            while (index < text.length) {
                val character = text[index++]
                when {
                    character == '"' -> return result.toString()
                    character == '\\' -> {
                        require(index < text.length) { "String escape is incomplete." }
                        when (val escape = text[index++]) {
                            '"', '\\', '/' -> result.append(escape)
                            'b' -> result.append('\b')
                            'f' -> result.append('\u000C')
                            'n' -> result.append('\n')
                            'r' -> result.append('\r')
                            't' -> result.append('\t')
                            'u' -> {
                                require(index + 4 <= text.length) { "Unicode escape is incomplete." }
                                val code = text.substring(index, index + 4).toIntOrNull(16)
                                    ?: throw IllegalArgumentException("Unicode escape is invalid.")
                                result.append(code.toChar())
                                index += 4
                            }
                            else -> throw IllegalArgumentException("String escape is invalid.")
                        }
                    }
                    character.code < 0x20 -> throw IllegalArgumentException("String contains a control character.")
                    else -> result.append(character)
                }
            }
            throw IllegalArgumentException("String is not terminated.")
        }

        private fun readNumber(): ServerCenterJsonValue.NumberValue {
            val start = index
            if (text[index] == '-') index++
            require(index < text.length) { "Number is incomplete." }
            if (text[index] == '0') index++ else {
                require(text[index] in '1'..'9') { "Number is invalid." }
                while (index < text.length && text[index].isDigit()) index++
            }
            if (index < text.length && text[index] == '.') {
                index++
                require(index < text.length && text[index].isDigit()) { "Number fraction is invalid." }
                while (index < text.length && text[index].isDigit()) index++
            }
            if (index < text.length && text[index] in "eE") {
                index++
                if (index < text.length && text[index] in "+-") index++
                require(index < text.length && text[index].isDigit()) { "Number exponent is invalid." }
                while (index < text.length && text[index].isDigit()) index++
            }
            return ServerCenterJsonValue.NumberValue(text.substring(start, index))
        }

        private fun <T : ServerCenterJsonValue> readLiteral(token: String, value: T): T {
            require(text.regionMatches(index, token, 0, token.length)) { "JSON literal is invalid." }
            index += token.length
            return value
        }

        private fun consume(character: Char): Boolean {
            if (index >= text.length || text[index] != character) return false
            index++
            return true
        }

        private fun skipWhitespace() {
            while (index < text.length && text[index] in " \t\r\n") index++
        }
    }

    private const val MAXIMUM_DEPTH = 12
}

internal fun ServerCenterJsonValue.asObject(): Map<String, ServerCenterJsonValue> =
    (this as? ServerCenterJsonValue.ObjectValue)?.fields
        ?: throw IllegalArgumentException("JSON value must be an object.")

internal fun ServerCenterJsonValue.asArray(): List<ServerCenterJsonValue> =
    (this as? ServerCenterJsonValue.ArrayValue)?.values
        ?: throw IllegalArgumentException("JSON value must be an array.")

internal fun ServerCenterJsonValue.asString(): String =
    (this as? ServerCenterJsonValue.StringValue)?.value
        ?: throw IllegalArgumentException("JSON value must be a string.")

internal fun ServerCenterJsonValue.asLong(): Long =
    (this as? ServerCenterJsonValue.NumberValue)?.token?.takeIf { !it.contains('.') && !it.contains('e', true) }
        ?.toLongOrNull() ?: throw IllegalArgumentException("JSON value must be an integer.")

internal fun ServerCenterJsonValue.asBoolean(): Boolean =
    (this as? ServerCenterJsonValue.BooleanValue)?.value
        ?: throw IllegalArgumentException("JSON value must be a boolean.")

internal fun Map<String, ServerCenterJsonValue>.required(name: String): ServerCenterJsonValue =
    this[name] ?: throw IllegalArgumentException("Required JSON field is missing: $name")

internal fun Map<String, ServerCenterJsonValue>.nullableString(name: String): String? = when (val value = this[name]) {
    null, ServerCenterJsonValue.NullValue -> null
    else -> value.asString()
}

internal fun Map<String, ServerCenterJsonValue>.nullableLong(name: String): Long? = when (val value = this[name]) {
    null, ServerCenterJsonValue.NullValue -> null
    else -> value.asLong()
}

internal fun Map<String, ServerCenterJsonValue>.nullableBoolean(name: String): Boolean? = when (val value = this[name]) {
    null, ServerCenterJsonValue.NullValue -> null
    else -> value.asBoolean()
}
