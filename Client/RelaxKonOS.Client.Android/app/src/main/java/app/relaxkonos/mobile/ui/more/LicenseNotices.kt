package app.relaxkonos.mobile.ui.more

internal data class LicenseNotice(val title: String, val blocks: List<String>)

/** Group the canonical notice by component, retaining every non-presentation paragraph. */
internal fun parseLicenseNotices(markdown: String): List<LicenseNotice> {
    val notices = mutableListOf<LicenseNotice>()
    var title = ""
    var blocks = mutableListOf<String>()
    var fenced = false
    val paragraph = StringBuilder()
    fun flushParagraph() {
        paragraph.toString().trim().takeIf { it.isNotEmpty() }?.let(blocks::add)
        paragraph.clear()
    }
    fun flushSection() {
        flushParagraph()
        if (title.isNotEmpty() || blocks.isNotEmpty()) notices += LicenseNotice(title, blocks.toList())
        blocks = mutableListOf()
    }
    val lines = markdown.lines()
    var tableHeaders = emptyList<String>()
    for (line in lines) {
        when {
            line.startsWith("```") -> { flushParagraph(); fenced = !fenced }
            fenced -> paragraph.appendLine(line)
            line.startsWith("### ") && title.isEmpty() -> {
                flushSection()
                title = line.removePrefix("### ").trim()
                tableHeaders = emptyList()
            }
            line.startsWith("# ") || line.startsWith("## ") -> {
                flushSection()
                title = line.substringAfter(' ').trim()
                tableHeaders = emptyList()
            }
            line.trim() == "---" || line.isBlank() -> flushParagraph()
            line.startsWith("|") -> {
                flushParagraph()
                val cells = line.trim().trim('|').split('|').map { it.trim() }
                if (tableHeaders.isEmpty()) {
                    flushSection()
                    title = ""
                    tableHeaders = cells
                }
                else if (!cells.all { it.matches(Regex("[:\\s-]+")) }) {
                    notices += LicenseNotice(
                        cells.first().replace("`", ""),
                        cells.drop(1).mapIndexed { index, value -> "${tableHeaders[index + 1]}：$value" },
                    )
                }
            }
            else -> paragraph.appendLine(line)
        }
    }
    flushSection()
    return notices
}

internal fun licenseDisplayText(text: String): String = text
    .removePrefix("### ")
    .replace(Regex("\\[([^]]+)]\\(([^)]+)\\)"), "$1 ($2)")
    .replace("**", "").replace("`", "")
