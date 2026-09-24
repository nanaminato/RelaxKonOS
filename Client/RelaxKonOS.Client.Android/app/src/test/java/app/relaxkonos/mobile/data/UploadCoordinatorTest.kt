package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.FileElevationGrant
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.core.net.UploadChunkResult
import app.relaxkonos.mobile.core.net.UploadProblemCodes
import app.relaxkonos.mobile.core.net.UploadSession
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.security.CredentialVault
import app.relaxkonos.mobile.security.FakeVaultCrypto
import app.relaxkonos.mobile.security.InMemoryVaultStorage
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileInputStream
import java.io.InputStream
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

private const val SERVER_KEY = "https://relaxkonos.local|nana|studio|device"
private const val SERVER_URL = "https://relaxkonos.local"
private const val DIRECTORY = "C:\\srv\\media"
private const val SESSION = "session-1"

/** Small enough to reach the server's smallest realistic chunk, large enough to need several chunks. */
private const val CHUNK = 65_536
private const val PAYLOAD_BYTES = 200_000

/**
 * The invariants of the resumable upload, checked where they actually live: between suspensions.
 *
 * Every check here runs on a virtual dispatcher with a zero backoff, so a retry budget can be exhausted
 * without waiting for it and a resynchronisation can be observed mid-flight. The coordinator is given the
 * test's own scope rather than its `backgroundScope`: the checks have to drive the transfer with
 * `advanceUntilIdle`, and work in a background scope is deliberately not something the test waits for.
 *
 * The one thing deliberately not covered is staging a document into the cache: that path writes through
 * `Dispatchers.IO` inside the transfer's own job, which a virtual scheduler cannot drive deterministically.
 * It is covered directly in [UploadSourceStagerTest] instead.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class UploadCoordinatorTest {
    @get:Rule
    val folder = TemporaryFolder()

    private val gateway = FakeGateway()
    private val session = AuthSession(gateway)
    private val elevations = ElevationRepository(
        gateway,
        session,
        CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto()),
    )
    // Lazily, not eagerly: the rule's folder does not exist yet while the test instance is constructed,
    // and these two have to keep their state across the whole test rather than being rebuilt per access.
    private val journal by lazy { UploadResumeJournal(File(folder.root, "upload-resume.txt")) }
    private val stager by lazy { UploadSourceStager(File(folder.root, "cache/uploads")) }

    private lateinit var payload: File

    /** What the coordinator reaches the bytes through when a "continue" is pressed after a restart. */
    private var reopenable: PickedDocument? = null
    private val documents = UploadDocumentOpener { uri -> reopenable?.takeIf { it.uri == uri } }

    private var coordinator: UploadCoordinator? = null

    @Before
    fun writePayload() {
        payload = File(folder.root, "video.mp4")
        payload.writeBytes(ByteArray(PAYLOAD_BYTES) { (it % 251).toByte() })
    }

    // ---- harness ---------------------------------------------------------------------------------

    private fun document(file: File = payload): PickedDocument = PickedDocument(
        uri = "content://docs/${file.name}",
        displayName = file.name,
        length = file.length(),
        lastModifiedMillis = file.lastModified(),
        open = { FileInputStream(file) },
        openAt = { offset -> FileInputStream(file).also { it.channel.position(offset) } },
    )

    private fun remoteSession(
        uploadId: String = SESSION,
        offset: Long = 0L,
        chunkSize: Int = CHUNK,
    ) = UploadSession(
        uploadId = uploadId,
        offset = offset,
        length = payload.length(),
        chunkSize = chunkSize,
        elevated = false,
        expiresAtMillis = null,
    )

    private fun entry(confirmedOffset: Long) = UploadResumeEntry(
        serverKey = SERVER_KEY,
        uploadId = SESSION,
        targetDirectoryPath = DIRECTORY,
        fileName = payload.name,
        sourceUri = document().uri,
        sourceKey = document().sourceKey,
        confirmedOffset = confirmedOffset,
        totalLength = payload.length(),
        // Now, not a fixed instant: the journal drops anything older than the server would keep a
        // session, so a hard-coded timestamp is a record that has already expired.
        recordedAtMillis = System.currentTimeMillis(),
    )

    private suspend fun signIn() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession(userName = "nana", workspaceName = "studio")) }
        session.login(SERVER_URL, "nana", "pw".toCharArray()) {}
    }

    private fun start(
        scope: CoroutineScope,
        dispatcher: CoroutineDispatcher,
        answers: ElevationAnswerProvider = ElevationAnswerProvider { _, _ -> null },
    ): UploadCoordinator {
        val created = UploadCoordinator(
            gateway = gateway,
            session = session,
            elevations = elevations,
            journal = journal,
            stager = stager,
            documents = documents,
            elevationAnswers = answers,
            serverKey = { SERVER_KEY },
            scope = scope,
            retryDelayMillis = { 0L },
            dispatcher = dispatcher,
        )
        coordinator = created
        return created
    }

    /** Reads exactly one chunk, as the transport would before writing it to the socket. */
    private fun drain(source: InputStream, count: Long): ByteArray {
        val out = ByteArrayOutputStream()
        val buffer = ByteArray(8_192)
        var remaining = count
        while (remaining > 0L) {
            val read = source.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
            if (read < 0) break
            out.write(buffer, 0, read)
            remaining -= read
        }
        return out.toByteArray()
    }

    /** A gateway that accepts every chunk at the offset it was offered. */
    private fun acceptAllChunks() {
        gateway.onSendUploadChunk = { _, _, _, offset, length, source, _ ->
            drain(source, length)
            UploadChunkResult.Confirmed(offset + length)
        }
        gateway.onCommitUpload = { _, _, _, _ -> ApiResult.Success(Unit) }
    }

    // ---- adoption ---------------------------------------------------------------------------------

    @Test
    fun `a resumed session continues from the offset the server reports, not the one remembered locally`() =
        runTest {
            signIn()
            val target = document()
            reopenable = target
            val remembered = entry(confirmedOffset = 2L * CHUNK)
            journal.record(remembered)
            var created = 0
            val offsets = mutableListOf<Long>()
            gateway.onCreateUploadSession = { _, _, _, _, _, _, _ ->
                created++
                ApiResult.Success(remoteSession())
            }
            // The journal believes two chunks arrived; the server says only one did.
            gateway.onUploadSession = { _, _, _ -> ApiResult.Success(remoteSession(offset = CHUNK.toLong())) }
            acceptAllChunks()
            gateway.onSendUploadChunk = { _, _, _, offset, length, source, _ ->
                drain(source, length)
                offsets += offset
                UploadChunkResult.Confirmed(offset + length)
            }

            start(this, StandardTestDispatcher(testScheduler)).resume(remembered)
            advanceUntilIdle()

            // The live session is adopted, never quietly replaced by a second one.
            assertEquals(0, created)
            // The local estimate is an estimate: the first chunk is re-sent from the server's own number.
            assertEquals(listOf(CHUNK.toLong(), 2L * CHUNK, 3L * CHUNK), offsets)
            val state = coordinator!!.state.value!!
            assertTrue(state.isFinished)
            assertTrue(state.resumed)
            assertTrue(journal.entries(SERVER_KEY).isEmpty())
        }

    @Test
    fun `a session the server cannot be asked about is not replaced by a second one`() = runTest {
        signIn()
        reopenable = document()
        val remembered = entry(confirmedOffset = CHUNK.toLong())
        journal.record(remembered)
        var created = 0
        gateway.onUploadSession = { _, _, _ -> ApiResult.Transport("no route to host") }
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ ->
            created++
            ApiResult.Success(remoteSession())
        }
        acceptAllChunks()

        start(this, StandardTestDispatcher(testScheduler)).resume(remembered)
        advanceUntilIdle()

        // Opening a second session for the same document while the first may still be alive would leave a
        // staging file on the server that nobody owns, so a failure to read the first one stops instead.
        assertEquals(0, created)
        assertEquals(UploadFailure.Network, coordinator!!.state.value?.failure)
        // Interrupted, not dead: the session and the entry stay, and "continue" is offered.
        assertEquals(listOf(SESSION), journal.entries(SERVER_KEY).map { it.uploadId })
    }

    @Test
    fun `a session the server has forgotten is replaced, once`() = runTest {
        signIn()
        reopenable = document()
        val remembered = entry(confirmedOffset = CHUNK.toLong())
        journal.record(remembered)
        var created = 0
        gateway.onUploadSession = { _, _, _ ->
            ApiResult.Problem(404, UploadProblemCodes.SESSION_NOT_FOUND, null)
        }
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ ->
            created++
            ApiResult.Success(remoteSession(uploadId = "fresh"))
        }
        acceptAllChunks()

        start(this, StandardTestDispatcher(testScheduler)).resume(remembered)
        advanceUntilIdle()

        assertEquals(1, created)
        assertTrue(coordinator!!.state.value!!.isFinished)
        assertEquals(listOf("fresh"), gateway.committedUploadIds)
    }

    @Test
    fun `a name the host will not accept is explained instead of folded into a generic refusal`() = runTest {
        signIn()
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ ->
            ApiResult.Problem(400, UploadProblemCodes.INVALID_FILE_NAME, null)
        }

        start(this, StandardTestDispatcher(testScheduler)).start(DIRECTORY, document())
        advanceUntilIdle()

        // A name that is legal on a phone can be illegal on the host the file lands on (a colon, a trailing
        // dot, a reserved device name), and the only remedy is renaming the file. "The server refused the
        // upload" gives the user nothing to act on, which is the whole point of a per-reason message.
        assertEquals(UploadFailure.NameUnusable, coordinator!!.state.value?.failure)
        assertFalse(UploadFailure.NameUnusable.keepsResumeEntry)
        assertTrue(journal.entries(SERVER_KEY).isEmpty())
    }

    // ---- offsets and progress ---------------------------------------------------------------------

    @Test
    fun `a chunk the server does not confirm never advances the offset, and the resync has a budget`() =
        runTest {
            signIn()
            var chunks = 0
            gateway.onCreateUploadSession = { _, _, _, _, _, _, _ -> ApiResult.Success(remoteSession()) }
            // The server accepts the request and then reports no new byte, however often it is asked.
            gateway.onSendUploadChunk = { _, _, _, _, length, source, _ ->
                chunks++
                drain(source, length)
                UploadChunkResult.Confirmed(0L)
            }
            gateway.onUploadSession = { _, _, _ -> ApiResult.Success(remoteSession(offset = 0L)) }

            start(this, StandardTestDispatcher(testScheduler)).start(DIRECTORY, document())
            advanceUntilIdle()

            // Counting a chunk the server cannot prove arrived is the one thing resumption exists to
            // prevent, so this resynchronises — bounded, rather than looping forever.
            assertEquals(coordinator!!.state.value.toString(), UploadCoordinator.MAXIMUM_CHUNK_FAILURES, chunks)
            assertEquals(UploadFailure.Network, coordinator!!.state.value?.failure)
            assertTrue(gateway.committedUploadIds.isEmpty())
        }

    @Test
    fun `a lower offset from the server is adopted without the bar going backwards`() = runTest {
        signIn()
        var reads = 0
        var sawCheck = false
        var refusedOnce = false
        val shown = mutableListOf<Long>()
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ -> ApiResult.Success(remoteSession()) }
        gateway.onUploadSession = { _, _, _ ->
            reads++
            if (coordinator!!.state.value?.resynchronising == true) sawCheck = true
            ApiResult.Success(remoteSession(offset = 0L))
        }
        gateway.onCommitUpload = { _, _, _, _ -> ApiResult.Success(Unit) }
        gateway.onSendUploadChunk = { _, _, _, offset, length, source, onInFlight ->
            drain(source, length)
            onInFlight?.invoke(length)
            shown += coordinator!!.state.value!!.displayedBytes
            if (!refusedOnce && offset == 2L * CHUNK) {
                // No offset in the refusal, which is the case that costs a round trip to the server.
                refusedOnce = true
                UploadChunkResult.Refused(409, UploadProblemCodes.OFFSET_MISMATCH, null)
            } else {
                UploadChunkResult.Confirmed(offset + length)
            }
        }

        start(this, StandardTestDispatcher(testScheduler)).start(DIRECTORY, document())
        advanceUntilIdle()

        assertTrue(refusedOnce)
        assertEquals(1, reads)
        // The card is told a check is running, because during the round trip there is nothing to show.
        assertTrue(sawCheck)
        // A resynchronisation may move the transfer backwards; the number the user is watching may not.
        assertEquals(shown, shown.sorted())
        assertEquals(payload.length(), shown.last())
        assertTrue(coordinator!!.state.value!!.isFinished)
    }

    @Test
    fun `a refusal that leaves the offset where it was cannot spin forever`() = runTest {
        signIn()
        var chunks = 0
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ -> ApiResult.Success(remoteSession()) }
        // "Not a failure" every time, naming the offset the client already has: without a budget for this
        // case the loop would never end, because every one of these answers is by definition normal.
        gateway.onSendUploadChunk = { _, _, _, _, length, source, _ ->
            chunks++
            drain(source, length)
            UploadChunkResult.Refused(409, UploadProblemCodes.OFFSET_MISMATCH, 0L)
        }

        start(this, StandardTestDispatcher(testScheduler)).start(DIRECTORY, document())
        advanceUntilIdle()

        assertEquals(UploadCoordinator.MAXIMUM_CHUNK_FAILURES, chunks)
        assertEquals(UploadFailure.Network, coordinator!!.state.value?.failure)
    }

    // ---- server-imposed limits --------------------------------------------------------------------

    @Test
    fun `a chunk is never larger than the size the server advertised`() = runTest {
        signIn()
        val ceiling = 32 * 1024
        val lengths = mutableListOf<Long>()
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ ->
            ApiResult.Success(remoteSession(chunkSize = ceiling))
        }
        gateway.onUploadSession = { _, _, _ -> ApiResult.Success(remoteSession(chunkSize = ceiling)) }
        gateway.onCommitUpload = { _, _, _, _ -> ApiResult.Success(Unit) }
        gateway.onSendUploadChunk = { _, _, _, offset, length, source, _ ->
            drain(source, length)
            lengths += length
            UploadChunkResult.Confirmed(offset + length)
        }

        start(this, StandardTestDispatcher(testScheduler)).start(DIRECTORY, document())
        advanceUntilIdle()

        // The server sizes its request-body limit to exactly this number, so a larger chunk is refused —
        // and a client that refused to shrink below its own floor would be refused every time.
        assertTrue(lengths.isNotEmpty())
        assertTrue(lengths.all { it <= ceiling })
        assertEquals(payload.length(), lengths.sum())
        assertTrue(coordinator!!.state.value!!.isFinished)
    }

    @Test
    fun `a document that changed while it was being sent is not offered for continuation`() = runTest {
        signIn()
        val target = document()
        reopenable = target
        val remembered = entry(confirmedOffset = 0L)
        journal.record(remembered)
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ -> ApiResult.Success(remoteSession()) }
        gateway.onUploadSession = { _, _, _ -> ApiResult.Success(remoteSession(offset = 0L)) }
        gateway.onCommitUpload = { _, _, _, _ -> ApiResult.Success(Unit) }
        gateway.onSendUploadChunk = { _, _, _, _, length, source, _ ->
            // The source stopped short of the bytes it declared: it is not the document the session holds.
            UploadChunkResult.SourceShort(deliveredBytes = length / 2)
        }

        start(this, StandardTestDispatcher(testScheduler)).resume(remembered)
        advanceUntilIdle()

        assertEquals(UploadFailure.SourceChanged, coordinator!!.state.value?.failure)
        // Publishing half of the old file followed by half of the new one is worse than starting again, so
        // the session is abandoned rather than offered as a "continue".
        assertTrue(journal.entries(SERVER_KEY).isEmpty())
        assertTrue(coordinator!!.resumable.value.isEmpty())
        assertTrue(gateway.committedUploadIds.isEmpty())
    }

    // ---- budget, pairing and cancellation ---------------------------------------------------------

    @Test
    fun `exhausting the retry budget keeps the session, the entry and the way to continue`() = runTest {
        signIn()
        var chunks = 0
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ -> ApiResult.Success(remoteSession()) }
        gateway.onUploadSession = { _, _, _ -> ApiResult.Success(remoteSession(offset = 0L)) }
        gateway.onSendUploadChunk = { _, _, _, _, length, source, _ ->
            chunks++
            drain(source, length)
            UploadChunkResult.Unreachable("no route to host")
        }

        start(this, StandardTestDispatcher(testScheduler)).start(DIRECTORY, document())
        advanceUntilIdle()

        assertEquals(UploadCoordinator.MAXIMUM_CHUNK_FAILURES, chunks)
        val state = coordinator!!.state.value!!
        assertEquals(UploadFailure.Network, state.failure)
        // The card finds the resume entry through this id; losing it would leave a continuable upload with
        // nothing but a dismiss button.
        assertEquals(SESSION, state.uploadId)
        assertEquals(listOf(SESSION), journal.entries(SERVER_KEY).map { it.uploadId })
        assertTrue(coordinator!!.resumable.value.any { it.uploadId == SESSION })
        // Nothing was abandoned: the session is exactly where the next attempt continues from.
        assertTrue(gateway.abortedUploadIds.isEmpty())
    }

    @Test
    fun `a finished upload clears the resume entry and its cache copy together`() = runTest {
        signIn()
        val target = document()
        reopenable = target
        val remembered = entry(confirmedOffset = 0L)
        journal.record(remembered)
        // A staged copy this entry explains, as it would exist after a document had to be copied first.
        val cacheCopy = stager.cacheFileFor(target.sourceKey)
        cacheCopy.parentFile?.mkdirs()
        cacheCopy.writeBytes(ByteArray(16))
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ -> ApiResult.Success(remoteSession()) }
        gateway.onUploadSession = { _, _, _ -> ApiResult.Success(remoteSession(offset = 0L)) }
        acceptAllChunks()

        start(this, StandardTestDispatcher(testScheduler)).resume(remembered)
        advanceUntilIdle()

        assertTrue(coordinator!!.state.value!!.isFinished)
        assertTrue(journal.entries(SERVER_KEY).isEmpty())
        assertFalse(cacheCopy.exists())
        assertTrue(coordinator!!.resumable.value.isEmpty())
        assertEquals(listOf(SESSION), gateway.committedUploadIds)
    }

    @Test
    fun `cancelling abandons the session and deletes the entry with its cache copy`() = runTest {
        signIn()
        val target = document()
        reopenable = target
        val remembered = entry(confirmedOffset = 0L)
        journal.record(remembered)
        val cacheCopy = stager.cacheFileFor(target.sourceKey)
        cacheCopy.parentFile?.mkdirs()
        cacheCopy.writeBytes(ByteArray(16))
        val parked = CompletableDeferred<Unit>()
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ -> ApiResult.Success(remoteSession()) }
        gateway.onUploadSession = { _, _, _ -> ApiResult.Success(remoteSession(offset = 0L)) }
        gateway.onAbortUpload = { _, _, _ -> ApiResult.Success(Unit) }
        gateway.onCommitUpload = { _, _, _, _ -> ApiResult.Success(Unit) }
        gateway.onSendUploadChunk = { _, _, _, _, length, source, _ ->
            drain(source, length)
            parked.await()
            UploadChunkResult.Confirmed(0L)
        }

        val uploads = start(this, StandardTestDispatcher(testScheduler))
        uploads.resume(remembered)
        advanceUntilIdle()
        assertTrue(uploads.isRunning)

        uploads.cancel()
        advanceUntilIdle()

        // An explicit stop is the one place a session is deleted, and the three things that describe it go
        // together: the server-side session, the entry that explains it, and the cache copy behind it.
        assertEquals(listOf(SESSION), gateway.abortedUploadIds)
        assertTrue(journal.entries(SERVER_KEY).isEmpty())
        assertFalse(cacheCopy.exists())
        assertTrue(uploads.resumable.value.isEmpty())
        assertNull(uploads.state.value)
        assertFalse(uploads.isRunning)
    }

    @Test
    fun `an entry for another server is dropped and never offered`() = runTest {
        signIn()
        val mine = entry(confirmedOffset = 0L)
        journal.record(mine)
        journal.record(mine.copy(uploadId = "theirs", serverKey = "https://other.local|bob|studio|device"))

        val uploads = start(this, StandardTestDispatcher(testScheduler))
        uploads.forgetOtherServers()

        assertEquals(listOf(SESSION), uploads.resumable.value.map { it.uploadId })
    }

    // ---- elevation --------------------------------------------------------------------------------

    @Test
    fun `elevation is settled once, when the session is opened, and never mid-transfer`() = runTest {
        signIn()
        var asks = 0
        var createCalls = 0
        gateway.onCreateUploadSession = { _, _, _, _, _, _, _ ->
            createCalls++
            if (createCalls == 1) ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) else ApiResult.Success(remoteSession())
        }
        gateway.onFileElevation = { _, _, _, _, _, _, _, _ ->
            ApiResult.Success(FileElevationGrant(requiresElevation = true, elevated = true, expiresAtMillis = null))
        }
        acceptAllChunks()

        start(
            this,
            StandardTestDispatcher(testScheduler),
            answers = ElevationAnswerProvider { _, _ ->
                asks++
                ElevationAnswer("admin", "pw".toCharArray())
            },
        ).start(DIRECTORY, document())
        advanceUntilIdle()

        // One question, asked before a single byte moved: a dialog appearing forty minutes into a transfer
        // is what fixing the decision at session creation exists to prevent.
        assertEquals(1, asks)
        assertEquals(2, createCalls)
        assertTrue(coordinator!!.state.value!!.isFinished)
    }
}
