package app.relaxkonos.mobile.servercenter

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.lang.reflect.Proxy
import java.util.zip.ZipInputStream
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class SshFileTransfersTest {
    @Test fun `ZIP export reports aggregate source bytes across multiple files`() = runTest {
        val sftp = MemorySftp()
        val first = sftp.file("/source/a.txt", "abc")
        val second = sftp.file("/source/b.txt", "12345")
        val progress = mutableListOf<Pair<Long, Long>>()
        SshFileTransfers(sftp.transport).exportZip(listOf(first, second), ByteArrayOutputStream()) { _, bytes, total ->
            progress += bytes to total
        }
        assertEquals(8L to 8L, progress.last())
        assertTrue(progress.zipWithNext().all { (a, b) -> b.first >= a.first })
        assertTrue(progress.all { it.second == 8L })
    }
    @Test fun `copy plans a bounded tree and creates directories before streamed files`() = runTest {
        val sftp = MemorySftp(); val root = sftp.directory("/source/folder"); sftp.file("/source/folder/a.txt", "abc")
        var dispatched = 0
        SshFileTransfers(sftp.transport).copy(listOf(root), "/destination", false) { dispatched++ }
        assertEquals(listOf("mkdir:/destination/folder", "copy:/destination/folder/a.txt"), sftp.writes)
        assertEquals("abc", sftp.contents["/destination/folder/a.txt"]!!.toString(Charsets.UTF_8)); assertEquals(2, dispatched)
    }
    @Test fun `cut uses root renames instead of copying bytes and deleting sources`() = runTest {
        val sftp = MemorySftp(); val file = sftp.file("/source/a.txt", "abc")
        SshFileTransfers(sftp.transport).copy(listOf(file), "/destination", true) { }
        assertEquals(listOf("rename:/source/a.txt:/destination/a.txt"), sftp.writes)
        assertFalse(sftp.entries.containsKey("/source/a.txt"))
    }
    @Test fun `existing destination and self descendant are rejected before mutation`() = runTest {
        val sftp = MemorySftp(); val root = sftp.directory("/source/folder")
        sftp.directory("/destination/folder")
        assertFalse(runCatching { SshFileTransfers(sftp.transport).copy(listOf(root), "/destination", false) { fail("No dispatch") } }.isSuccess)
        assertFalse(runCatching { SshFileTransfers(sftp.transport).copy(listOf(root), root.path, false) { fail("No dispatch") } }.isSuccess)
        assertTrue(sftp.writes.isEmpty())
    }
    @Test fun `metadata changes and symlinks never supply a copied or exported source`() = runTest {
        val sftp = MemorySftp(); val source = sftp.file("/source/a.txt", "abc")
        sftp.entries[source.path] = source.copy(size = 4)
        assertFalse(runCatching { SshFileTransfers(sftp.transport).plan(listOf(source)) }.isSuccess)
        val link = source.copy(isSymbolicLink = true); sftp.entries[source.path] = link
        assertFalse(runCatching { SshFileTransfers(sftp.transport).exportZip(listOf(link), ByteArrayOutputStream()) }.isSuccess)
        assertTrue(sftp.writes.isEmpty())
    }
    @Test fun `entry depth and total byte limits are checked before writes`() = runTest {
        val many = MemorySftp(); val root = many.directory("/source/folder")
        repeat(1000) { many.file("/source/folder/f$it", "") }
        assertFalse(runCatching { SshFileTransfers(many.transport).plan(listOf(root)) }.isSuccess)
        val deep = MemorySftp(); var path = "/source/folder"; val initial = deep.directory(path)
        repeat(33) { path += "/d"; deep.directory(path) }
        assertFalse(runCatching { SshFileTransfers(deep.transport).plan(listOf(initial)) }.isSuccess)
        val large = many.file("/source/large", "").copy(size = SshFileTransferRules.MAX_BYTES + 1); many.entries[large.path] = large
        assertFalse(runCatching { SshFileTransfers(many.transport).plan(listOf(large)) }.isSuccess)
        assertTrue(many.writes.isEmpty()); assertTrue(deep.writes.isEmpty())
    }
    @Test fun `ZIP exports exact file bytes and safe relative names`() = runTest {
        val sftp = MemorySftp(); val root = sftp.directory("/source/folder"); sftp.file("/source/folder/a.txt", "abc")
        val out = ByteArrayOutputStream(); SshFileTransfers(sftp.transport).exportZip(listOf(root), out)
        ZipInputStream(ByteArrayInputStream(out.toByteArray())).use { zip ->
            assertEquals("folder/", zip.nextEntry.name); zip.closeEntry()
            assertEquals("folder/a.txt", zip.nextEntry.name); assertEquals("abc", zip.readBytes().toString(Charsets.UTF_8)); assertNull(zip.nextEntry)
        }
    }
    @Test fun `actual byte growth is rejected by input and output budgets`() {
        val input = SshBoundedInputStream(ByteArrayInputStream(byteArrayOf(1, 2, 3)), 2)
        assertEquals(1, input.read()); assertEquals(2, input.read()); assertFalse(runCatching { input.read() }.isSuccess)
        val sink = ByteArrayOutputStream(); val output = SshBoundedOutputStream(sink, 2)
        output.write(byteArrayOf(1, 2)); assertFalse(runCatching { output.write(3) }.isSuccess); assertEquals(2, sink.size())
        listOf("..", "a/b", "a\\b", "a\n", "").forEach { assertFalse(SshFileTransferRules.safeName(it)) }
    }
    @Test fun `unreadable destination is not interpreted as absent`() = runTest {
        val sftp = MemorySftp(); val source = sftp.file("/source/a.txt", "abc"); sftp.unreadable = "/destination/a.txt"
        assertFalse(runCatching { SshFileTransfers(sftp.transport).copy(listOf(source), "/destination", false) { fail("No dispatch") } }.isSuccess)
        assertTrue(sftp.writes.isEmpty())
    }
}

private class MemorySftp {
    val entries = linkedMapOf<String, SshFileEntry>()
    val contents = mutableMapOf<String, ByteArray>()
    val writes = mutableListOf<String>()
    var unreadable: String? = null
    fun directory(path: String) = SshFileEntry(path, path.substringAfterLast('/'), true, false).also { entries[path] = it }
    fun file(path: String, content: String) = SshFileEntry(path, path.substringAfterLast('/'), false, false, content.toByteArray().size.toLong()).also {
        entries[path] = it; contents[path] = content.toByteArray()
    }
    val transport = Proxy.newProxyInstance(ServerCenterSshTransport::class.java.classLoader, arrayOf(ServerCenterSshTransport::class.java)) { _, method, arguments ->
        val args = arguments ?: emptyArray()
        when (method.name) {
            "fileInfo" -> { if (args[0] == unreadable) error("Permission denied"); entries[args[0] as String] }
            "listDirectory" -> entries.values.filter { it.path.substringBeforeLast('/') == args[0] }
            "createDirectory" -> { val path = args[0] as String; directory(path); writes += "mkdir:$path"; Unit }
            "copyFile" -> {
                val source = args[0] as String; val destination = args[1] as String
                file(destination, contents[source]!!.toString(Charsets.UTF_8)); writes += "copy:$destination"; Unit
            }
            "rename" -> {
                val source = args[0] as String; val destination = args[1] as String
                entries.remove(source)?.let { entries[destination] = it.copy(path = destination, name = destination.substringAfterLast('/')) }
                contents.remove(source)?.let { contents[destination] = it }; writes += "rename:$source:$destination"; Unit
            }
            "download" -> { (args[1] as java.io.OutputStream).write(contents[args[0] as String]!!); Unit }
            else -> error("Unexpected transport primitive: ${method.name}")
        }
    } as ServerCenterSshTransport
}
