package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.RemoteEntry
import java.util.Locale

enum class FileSort { Name, Modified, Size, Type }
enum class FileBatchAction { Copy, Move, Delete }

object FileBrowserPolicy {
    const val MAX_BATCH = 500
    fun validName(name: String): Boolean = name.isNotBlank() && name !in setOf(".", "..") &&
        name.none { it == '/' || it == '\\' || it == '\u0000' }
    fun isRoot(path: String): Boolean = path == "/" || path == "\\" ||
        (path.length == 3 && path[1] == ':' && path[2] in charArrayOf('/', '\\'))
    fun mutable(entry: RemoteEntry): Boolean = !entry.isDrive && !isRoot(entry.path)
    fun visible(entries: List<RemoteEntry>, query: String, hidden: Boolean, sort: FileSort, descending: Boolean): List<RemoteEntry> {
        val order = Comparator<RemoteEntry> { a, b ->
            when {
                a.isDirectory != b.isDirectory -> if (a.isDirectory) -1 else 1
                else -> {
                    val primary = when (sort) {
                        FileSort.Name -> a.name.lowercase(Locale.ROOT).compareTo(b.name.lowercase(Locale.ROOT))
                        FileSort.Modified -> compareValues(a.modifiedAtMillis, b.modifiedAtMillis)
                        FileSort.Size -> compareValues(a.sizeBytes, b.sizeBytes)
                        FileSort.Type -> a.name.substringAfterLast('.', "").lowercase(Locale.ROOT)
                            .compareTo(b.name.substringAfterLast('.', "").lowercase(Locale.ROOT))
                    }.let { if (descending) -it else it }
                    if (primary != 0) primary else a.path.compareTo(b.path)
                }
            }
        }
        return entries.filter { (hidden || !it.isHidden) && it.name.contains(query, ignoreCase = true) }.sortedWith(order)
    }
    fun snapshot(entries: List<RemoteEntry>): List<RemoteEntry> {
        require(entries.isNotEmpty() && entries.size <= MAX_BATCH && entries.all(::mutable))
        val unique = entries.distinctBy { canonical(it.path) }
        // A selected directory already contains its descendants; do not issue the same work twice.
        return unique.filter { entry -> unique.none { parent -> parent.isDirectory && canonical(parent.path) != canonical(entry.path) &&
            canonical(entry.path).startsWith(canonical(parent.path).trimEnd('/') + "/") } }
    }
    fun validRemotePath(path: String): Boolean = path.isNotBlank() && '\u0000' !in path &&
        (path.startsWith('/') || path.startsWith("\\\\") || (path.length >= 3 && path[0].isLetter() && path[1] == ':' && path[2] in charArrayOf('/', '\\'))) &&
        path.split('/', '\\').none { it == "." || it == ".." }
    fun validDestination(entries: List<RemoteEntry>, destinationDirectory: String): Boolean = validRemotePath(destinationDirectory) &&
        entries.none { source -> canonical(source.path) == canonical(destinationDirectory) || (source.isDirectory &&
            canonical(destinationDirectory).startsWith(canonical(source.path).trimEnd('/') + "/")) }
    private fun canonical(path: String): String {
        val windows = path.contains('\\') || (path.length > 1 && path[1] == ':')
        val separators = path.replace('\\', '/').replace(Regex("/+"), "/")
        return if (windows) separators.lowercase(Locale.ROOT) else separators
    }
    fun parseMode(value: String): Int? = value.takeIf { it.length in 3..4 && it.all { c -> c in '0'..'7' } }?.toInt(8)
    fun formatMode(mode: Int): String = mode.toString(8).padStart(4, '0')
}
