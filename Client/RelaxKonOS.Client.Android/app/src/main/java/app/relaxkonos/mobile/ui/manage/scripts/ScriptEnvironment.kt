package app.relaxkonos.mobile.ui.manage.scripts

/** Reject ambiguous or unsupported entries without silently replacing an earlier value. */
internal fun scriptEnvironment(text: String): Map<String, String>? {
    val entries = text.lines().filter(String::isNotBlank)
    if (entries.size > 32) return null
    val values = linkedMapOf<String, String>()
    for (line in entries) {
        if ('=' !in line) return null
        val key = line.substringBefore('=')
        val value = line.substringAfter('=')
        if (key.length !in 1..128 || !key.matches(Regex("[A-Za-z_][A-Za-z0-9_]*")) ||
            key in values || value.length > 4096 || '\u0000' in value) return null
        values[key] = value
    }
    return values
}
