package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.FileElevationGrant
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.security.CredentialVault
import app.relaxkonos.mobile.security.FakeVaultCrypto
import app.relaxkonos.mobile.security.InMemoryVaultStorage
import java.io.ByteArrayInputStream
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class FilesRepositoryTest {
    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val elevations = ElevationRepository(gateway, session, CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()))
    private val repository = FilesRepository(gateway, session, elevations)

    private suspend fun signIn() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login("https://relaxkonos.local", "nana", "pw".toCharArray()) {}
    }

    @Test
    fun `windows paths preserve their drive root when navigating and creating`() {
        assertEquals("C:\\", repository.parentOf("C:\\work"))
        assertEquals("C:\\", repository.parentOf("C:\\"))
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
}
