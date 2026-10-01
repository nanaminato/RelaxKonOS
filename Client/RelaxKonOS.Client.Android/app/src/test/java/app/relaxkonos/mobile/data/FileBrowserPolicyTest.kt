package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.net.RemoteEntry
import org.junit.Assert.*
import org.junit.Test

class FileBrowserPolicyTest {
    private fun entry(name: String, folder: Boolean = false, path: String = "/$name", hidden: Boolean = false,
        size: Long? = null, modified: Long? = null) = RemoteEntry(path, name, folder, size, modified, null, isHidden = hidden)
    @Test fun `filter uses host hidden metadata and preserves directories first under every sort`() {
        val entries = listOf(entry("a.txt", size = 100), entry(".visible"), entry("hidden", hidden = true), entry("z", true), entry("b.png", size = 1))
        assertFalse(FileBrowserPolicy.visible(entries, "", false, FileSort.Name, false).any { it.name == "hidden" })
        assertTrue(FileBrowserPolicy.visible(entries, "", false, FileSort.Name, false).any { it.name == ".visible" })
        FileSort.entries.forEach { sort -> assertEquals("z", FileBrowserPolicy.visible(entries, "", true, sort, true).first().name) }
        assertEquals(listOf("a.txt"), FileBrowserPolicy.visible(entries, "A.T", true, FileSort.Name, false).map { it.name })
        assertEquals(listOf("a.txt", "b.png"), FileBrowserPolicy.visible(entries.filterNot { it.isDirectory || it.name.startsWith('.') || it.isHidden }, "", true, FileSort.Size, true).map { it.name })
    }
    @Test fun `batch snapshot removes descendants and duplicates but keeps prefix siblings`() {
        val entries = listOf(entry("a", true), entry("child", path = "/a/child"), entry("sibling", path = "/ab/child"), entry("a", true))
        assertEquals(listOf("/a", "/ab/child"), FileBrowserPolicy.snapshot(entries).map { it.path })
        val windows = listOf(entry("a", true, "C:\\A"), entry("child", path = "c:/a/child"))
        assertEquals(listOf("C:\\A"), FileBrowserPolicy.snapshot(windows).map { it.path })
    }
    @Test fun `roots bounds and self descendant destinations are refused`() {
        assertTrue(runCatching { FileBrowserPolicy.snapshot(emptyList()) }.isFailure)
        assertTrue(runCatching { FileBrowserPolicy.snapshot(List(501) { entry("$it") }) }.isFailure)
        listOf("/", "C:\\", "D:/").forEach { assertFalse(FileBrowserPolicy.mutable(entry("root", true, it))) }
        assertFalse(FileBrowserPolicy.mutable(entry("drive", true).copy(isDrive = true)))
        assertFalse(FileBrowserPolicy.validDestination(listOf(entry("a", true)), "/a/b"))
        assertFalse(FileBrowserPolicy.validDestination(listOf(entry("a", true, "C:\\A")), "c:/a/B"))
        assertTrue(FileBrowserPolicy.validDestination(listOf(entry("a", true)), "/ab"))
        assertFalse(FileBrowserPolicy.validDestination(listOf(entry("a", true)), "/other/../a/child"))
        assertFalse(FileBrowserPolicy.validDestination(listOf(entry("a", true)), "//a//child"))
        assertFalse(FileBrowserPolicy.validRemotePath("relative/path"))
        assertTrue(FileBrowserPolicy.validRemotePath("C:\\dst\\ 文件 * "))
    }
    @Test fun `names keep leading and trailing spaces and octal input covers special bits`() {
        listOf(".", "..", "", " ", "a/b", "a\\b", "a\u0000b").forEach { assertFalse(FileBrowserPolicy.validName(it)) }
        assertTrue(FileBrowserPolicy.validName(" 文件 * "))
        assertEquals(0xfff, FileBrowserPolicy.parseMode("7777"))
        assertEquals(0x1a4, FileBrowserPolicy.parseMode("644"))
        listOf("88", "0788", "10000", " 644", "0x644").forEach { assertNull(FileBrowserPolicy.parseMode(it)) }
        assertEquals("4755", FileBrowserPolicy.formatMode(0x9ed))
    }
}
