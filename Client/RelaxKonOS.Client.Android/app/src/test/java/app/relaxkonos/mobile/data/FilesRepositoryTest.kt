package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DownloadSink
import app.relaxkonos.mobile.core.net.FileElevationGrant
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.security.CredentialVault
import app.relaxkonos.mobile.security.FakeVaultCrypto
import app.relaxkonos.mobile.security.InMemoryVaultStorage
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.assertFalse
import org.junit.Test

class FilesRepositoryTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val elevations = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()))
    private val repository = FilesRepository(gateway, session, elevations)

    private suspend fun signIn() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(ServerConnectionIdentityRules.direct("https://relaxkonos.local"), "nana", "pw".toCharArray()) {}
    }

    @Test
    fun `windows drive roots use distinct operation and navigation parents`() {
        assertEquals("C:\\", repository.parentOf("C:\\work"))
        assertEquals("C:\\", repository.parentOf("C:\\"))
        assertEquals("", repository.navigationParentOf("C:\\"))
        assertEquals("C:\\", repository.navigationParentOf("C:\\work"))
        assertEquals("C:\\work\\new", repository.childOf("C:\\work", "new"))
        assertEquals("/etc/new", repository.childOf("/etc", "new"))
    }

    @Test
    fun `move elevates only source and destination directories before retrying`() = runTest {
        signIn()
        var attempts = 0
        gateway.onMove = { _, _, source, destination ->
            assertEquals("/srv/a/report.txt", source)
            assertEquals("/srv/b/report.txt", destination)
            attempts++
            if (attempts == 1) ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) else ApiResult.Success(Unit)
        }
        gateway.onFileElevation = { _, _, path, capability, _, related, descendants, _ ->
            assertEquals("/srv/a", path)
            assertEquals("move", capability)
            assertEquals(listOf("/srv/b"), related)
            assertTrue(descendants)
            ApiResult.Success(FileElevationGrant(requiresElevation = true, elevated = true, expiresAtMillis = null))
        }

        val result = repository.move("/srv/a/report.txt", "/srv/b/report.txt", ElevationAnswerProvider { _, _ ->
            ElevationAnswer("admin", "pw".toCharArray())
        })

        assertTrue(result is ApiResult.Success)
        assertEquals(2, attempts)
    }

    @Test
    fun `upload passes the saf stream and reports request-body progress`() = runTest {
        signIn()
        val progress = mutableListOf<Long>()
        gateway.onUpload = { _, _, directory, name, source, length, callback ->
            assertEquals("/srv/drop", directory)
            assertEquals("note.txt", name)
            assertEquals(4L, length)
            assertEquals("note", source.bufferedReader().readText())
            callback?.invoke(4)
            ApiResult.Success(Unit)
        }

        val result = repository.upload(
            targetDirectoryPath = "/srv/drop",
            fileName = "note.txt",
            source = ByteArrayInputStream("note".toByteArray()),
            contentLength = 4,
            provider = ElevationAnswerProvider { _, _ -> error("no elevation should be requested") },
            onProgress = progress::add,
        )

        assertTrue(result is ApiResult.Success)
        assertEquals(listOf(4L), progress)
    }

    @Test
    fun `download writes the body into the caller's sink and reports progress`() = runTest {
        signIn()
        val progress = mutableListOf<Pair<Long, Long?>>()
        val destination = ByteArrayOutputStream()
        gateway.onDownload = { _, _, path, sink, callback ->
            assertEquals("/srv/drop/note.txt", path)
            sink.open().use { it.write("note".toByteArray()) }
            callback?.invoke(4, 4L)
            ApiResult.Success(4)
        }

        val result = repository.download(
            path = "/srv/drop/note.txt",
            target = DownloadSink { destination },
            provider = ElevationAnswerProvider { _, _ -> error("no elevation should be requested") },
            onProgress = { written, total -> progress += written to total },
        )

        assertTrue(result is ApiResult.Success)
        assertEquals("note", destination.toString(Charsets.UTF_8.name()))
        assertEquals(listOf(4L to 4L), progress)
    }

    @Test
    fun `thumbnail passes the requested edge through and hands back the bytes`() = runTest {
        signIn()
        gateway.onThumbnail = { _, _, path, maxEdge ->
            assertEquals("/srv/shots/holiday.jpg", path)
            assertEquals(320, maxEdge)
            ApiResult.Success(byteArrayOf(1, 2, 3))
        }

        val result = repository.thumbnail(
            path = "/srv/shots/holiday.jpg",
            maxEdge = 320,
            provider = ElevationAnswerProvider { _, _ -> error("no elevation should be requested") },
        )

        // The bytes themselves, not a byte count: a thumbnail has no destination but memory.
        assertEquals(listOf<Byte>(1, 2, 3), (result as ApiResult.Success).value.toList())
    }

    @Test
    fun `thumbnail reads the file's own path and elevates it like a download`() = runTest {
        signIn()
        var attempts = 0
        gateway.onThumbnail = { _, _, _, _ ->
            attempts++
            if (attempts == 1) ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) else ApiResult.Success(byteArrayOf(9))
        }
        gateway.onFileElevation = { _, _, path, capability, _, related, descendants, _ ->
            // The file itself, not its directory: a thumbnail reads exactly what a download reads, so
            // it must never be granted a wider scope than the download it precedes.
            assertEquals("/srv/shots/holiday.jpg", path)
            assertEquals("read", capability)
            assertEquals(emptyList<String>(), related)
            assertTrue(!descendants)
            ApiResult.Success(FileElevationGrant(requiresElevation = true, elevated = true, expiresAtMillis = null))
        }

        val result = repository.thumbnail("/srv/shots/holiday.jpg", 320, ElevationAnswerProvider { _, _ ->
            ElevationAnswer("admin", "pw".toCharArray())
        })

        assertTrue(result is ApiResult.Success)
        assertEquals(2, attempts)
    }
    @Test fun `permissions elevate exactly one entry with write capability and preserve special bits`() = runTest {
        signIn(); var sends = 0
        gateway.onSetFilePermissions = { _, _, path, mode ->
            assertEquals("/srv/ 文件 * ", path); assertEquals(0x9ed, mode); sends++
            if (sends == 1) ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null)
            else ApiResult.Success(app.relaxkonos.mobile.core.net.RemoteFileProperties(path, " 文件 * ", false, 4, null, null, "-rwsr-xr-x", mode))
        }
        gateway.onFileElevation = { _, _, path, capability, _, related, descendants, _ ->
            assertEquals("/srv/ 文件 * ", path); assertEquals("write", capability); assertFalse(descendants); assertTrue(related.isEmpty())
            ApiResult.Success(FileElevationGrant(true, true, null))
        }
        assertTrue(repository.setPermissions("/srv/ 文件 * ", 0x9ed, ElevationAnswerProvider { _, _ -> ElevationAnswer("admin", "pw".toCharArray()) }) is ApiResult.Success)
        assertEquals(2, sends)
    }
    @Test fun `properties never raise authorization without explicit request and stale owner cannot retry`() = runTest {
        signIn(); var prompts = 0; var sends = 0
        gateway.onFileProperties = { _, _, _ -> sends++; ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) }
        assertTrue(repository.properties("/srv/a") is ApiResult.Problem); assertEquals(1, sends)
        val result = runCatching { repository.properties("/srv/a", ElevationAnswerProvider { _, _ -> prompts++; signIn(); ElevationAnswer("admin", "pw".toCharArray()) }) }
        assertTrue(result.exceptionOrNull() is kotlinx.coroutines.CancellationException)
        assertEquals(1, prompts); assertEquals(2, sends)
    }

}
