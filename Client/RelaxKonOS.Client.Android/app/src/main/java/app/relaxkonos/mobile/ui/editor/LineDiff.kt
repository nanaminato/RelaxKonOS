package app.relaxkonos.mobile.ui.editor

/** A bounded line comparison for review; saving still uses the server's exact byte version. */
internal fun lineDiff(original: String, edited: String): String {
    val before = original.lines()
    val after = edited.lines()
    val prefix = before.zip(after).takeWhile { it.first == it.second }.size
    val suffix = before.drop(prefix).asReversed().zip(after.drop(prefix).asReversed())
    .takeWhile { it.first == it.second }.size
    val removed = before.subList(prefix, before.size - suffix)
    val added = after.subList(prefix, after.size - suffix)
    val lines = (removed.map { "− $it" } + added.map { "+ $it" })
    return lines.take(120).joinToString("\n") + if (lines.size > 120) "\n…" else ""
}
