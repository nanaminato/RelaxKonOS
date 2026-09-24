package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.FileElevationCapabilities
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.core.net.UploadChunkResult
import app.relaxkonos.mobile.core.net.UploadProblemCodes
import app.relaxkonos.mobile.core.net.UploadProtocol
import app.relaxkonos.mobile.core.net.isSessionExpired
import java.util.UUID
import java.util.concurrent.ThreadLocalRandom
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** The stage of one upload, as the transfer card shows it. */
enum class UploadStage {
    /**
     * Copying a document that cannot be read from an offset into the private cache.
     *
     * A stage of its own, never a fraction of the transfer: counting it as progress would claim data
     * has reached the server while it has not yet left the device.
     */
    Preparing,
    Uploading,
    Committing,

    /** The destination file exists. The card is removed once this has been reported. */
    Finished,
}

/** Why an upload stopped, in terms the user can act on rather than as an I/O message. */
enum class UploadFailure {
    /** Interrupted. Progress is kept and the transfer can be continued. */
    Network,

    /** The server has no room for the file. */
    ServerStorage,

    /** This device has no room to stage the document. */
    LocalCache,

    /** The document changed while it was being sent, so the transfer must start again. */
    SourceChanged,

    /** The document can no longer be opened; it has to be picked again. */
    SourceUnreadable,

    /** The destination needs administrator authorization that was refused or unavailable. */
    ElevationRequired,

    /** The session was refused by the server and could not be rebuilt. */
    SessionLost,

    /**
     * The server refused the file name itself (`400 invalid-file-name`).
     *
     * A name that is legal on the phone can be illegal on the host the file lands on (a colon, a trailing
     * dot, a reserved device name), and the only fix is to rename the file. Without its own category the
     * user is told "the server refused the upload" and has nothing to act on.
     */
    NameUnusable,

    /** A named refusal with no better category. */
    Server,
    ;

    /**
     * Whether the upload can still be continued into the session it was using.
     *
     * A resume entry exists to explain a live session. Keeping one whose session the server no longer has,
     * or whose source is no longer the source it was opened against, offers the user a "continue" that
     * cannot succeed — and for a changed document it would publish the first half of the old file
     * followed by the second half of the new one. Both are worse than saying "start again".
     */
    val keepsResumeEntry: Boolean
        get() = when (this) {
            Network, Server, ServerStorage, LocalCache, ElevationRequired -> true
            SourceChanged, SourceUnreadable, SessionLost, NameUnusable -> false
        }
}

/**
 * What the transfer card renders.
 *
 * [confirmedBytes] and [inFlightBytes] are kept apart because they answer different questions: only the
 * first is data the server holds, and only their sum is what a bar may show. The coordinator never emits
 * a smaller sum than it has already emitted, which is what keeps a resynchronisation from rewinding the
 * bar while the bytes are still there.
 */
data class UploadState(
    val fileName: String,
    val stage: UploadStage,
    val confirmedBytes: Long,
    val inFlightBytes: Long,
    val totalBytes: Long?,
    /**
     * The server session this transfer is using.
     *
     * Carried so the card can tell which of the unfinished uploads the failure it is showing belongs to:
     * the resume entry is what "continue" needs, and it is looked up by this id rather than guessed from
     * the file name.
     */
    val uploadId: String? = null,
    /** True when this transfer was started from a resume entry rather than a fresh pick. */
    val resumed: Boolean = false,
    /**
     * True while the coordinator is asking the server what it actually holds.
     *
     * Published as its own fact rather than as a byte count, because during the round trip nobody knows
     * the answer: the local estimate is not evidence and the bar is not allowed to move on a guess. The
     * card says "checking with the server" for exactly as long as this is set
     * (`RelaxKonOS.FileUpload.Design.md` §5.4).
     */
    val resynchronising: Boolean = false,
    val failure: UploadFailure? = null,
) {
    /** Bytes confirmed by the server plus those already handed to the socket, capped at the length. */
    val displayedBytes: Long
        get() = totalBytes?.let { minOf(confirmedBytes + inFlightBytes, it) } ?: (confirmedBytes + inFlightBytes)

    val progress: Float?
        get() = totalBytes?.takeIf { it > 0L }?.let { (displayedBytes.toFloat() / it).coerceIn(0f, 1f) }

    /** True while the coordinator is still working on this transfer. */
    val isRunning: Boolean get() = failure == null && stage != UploadStage.Finished

    val isFinished: Boolean get() = failure == null && stage == UploadStage.Finished
}

/**
 * Runs resumable uploads, one at a time, for a lifetime longer than any screen.
 *
 * This exists outside a ViewModel for one reason: a multi-gigabyte transfer must not be tied to the page
 * that started it, and it must be able to answer "where was I" after the process was killed. The rules it
 * enforces are the protocol's:
 *
 * - every doubt about what the server received is resolved by asking it, never by assuming an in-flight
 *   chunk arrived;
 * - an offset reported by the server is adopted even when it is lower than the local estimate — the local
 *   view is an estimate, the server's number is the truth;
 * - exhausting the retry budget keeps the session and the resume entry, so "continue" resumes instead of
 *   starting a second transfer from zero;
 * - a cache copy is deleted exactly when the resume entry that explains it is deleted.
 */
class UploadCoordinator(
    private val gateway: RelaxKonGateway,
    private val session: AuthSession,
    private val elevations: ElevationRepository,
    private val journal: UploadResumeJournal,
    private val stager: UploadSourceStager,
    private val documents: UploadDocumentOpener,
    private val elevationAnswers: ElevationAnswerProvider,
    /** Server / user / workspace / device key. A resume entry belongs to exactly one. */
    private val serverKey: () -> String?,
    private val scope: CoroutineScope,
    /** Backoff between chunk retries. Injectable so a check can exhaust the budget without waiting. */
    private val retryDelayMillis: (Int) -> Long = ::defaultRetryDelayMillis,
    /**
     * Where the transfer runs. Injectable for the same reason as the backoff: the whole component is
     * about what happens between suspensions — which offset is adopted after a lost answer, what a retry
     * budget's exhaustion leaves behind — and none of that is observable from a check that waits on real
     * I/O and real seconds.
     */
    private val dispatcher: CoroutineDispatcher = Dispatchers.IO,
) {
    private val stateFlow = MutableStateFlow<UploadState?>(null)

    /** The transfer in flight, or the last one that stopped. `null` before the first upload. */
    val state: StateFlow<UploadState?> = stateFlow.asStateFlow()

    private val resumableFlow = MutableStateFlow<List<UploadResumeEntry>>(emptyList())

    /** Unfinished uploads this device can continue, newest first. */
    val resumable: StateFlow<List<UploadResumeEntry>> = resumableFlow.asStateFlow()

    private var job: Job? = null

    /** The session the running transfer is using, so [cancel] can abandon exactly that one. */
    private var activeUploadId: String? = null

    /** Highest byte count shown so far, so a resynchronisation can never rewind the bar. */
    private var shownBytes = 0L

    /** True while a transfer is in flight; the picker is disabled then, since only one runs at a time. */
    val isRunning: Boolean get() = job?.isActive == true

    /** Starts a fresh upload of a document the user just picked. */
    fun start(targetDirectoryPath: String, document: PickedDocument) {
        launch(targetDirectoryPath, document, resume = null)
    }

    /**
     * Continues an unfinished upload.
     *
     * The document is re-opened from the URI the entry remembers. A picker's grant does not survive a
     * process restart, so a document that can no longer be read is reported as having to be picked again
     * rather than as a transfer failure — the difference is what the user has to do about it.
     */
    fun resume(entry: UploadResumeEntry) {
        val document = runCatching { documents.open(entry.sourceUri) }.getOrNull()
        if (document == null) {
            failWith(entry.fileName, UploadFailure.SourceUnreadable)
            journal.remove(entry.uploadId)?.let { stager.discardCache(it.sourceKey) }
            refreshResumable()
            return
        }
        launch(entry.targetDirectoryPath, document, resume = entry)
    }

    /**
     * Stops the transfer in flight and abandons it.
     *
     * Cancelling is explicit here, not a side effect of a screen going away: the session is deleted, the
     * resume entry and the cache copy go with it, and the destination directory is left with nothing —
     * only a commit can create the file. A transfer that merely failed keeps all three instead, which is
     * what makes "continue" possible.
     */
    fun cancel() {
        val running = job ?: return
        val uploadId = activeUploadId
        running.cancel()
        if (uploadId == null) return
        // The abandon work belongs to the coordinator, not to the job being cancelled: running it inside
        // that job would stop it at the first suspension point.
        scope.launch {
            withContext(NonCancellable) {
                abortSession(uploadId)
                journal.remove(uploadId)?.let { stager.discardCache(it.sourceKey) }
            }
            stateFlow.value = null
            refreshResumable()
        }
    }

    /** Abandons an unfinished upload for good: session, entry and cache copy all go. */
    fun discard(entry: UploadResumeEntry) {
        if (job?.isActive == true) return
        scope.launch {
            withContext(NonCancellable) {
                abortSession(entry.uploadId)
                journal.remove(entry.uploadId)?.let { stager.discardCache(it.sourceKey) }
            }
            if (stateFlow.value?.failure != null) stateFlow.value = null
            refreshResumable()
        }
    }

    /** Clears a finished or failed card. A running transfer is not dismissed by this. */
    fun dismiss() {
        if (job?.isActive == true) return
        stateFlow.value = null
    }

    /** Loads the resume list and drops any cache copy no entry owns. Called when a session becomes active. */
    fun restore() {
        refreshResumable()
        trimOrphanedCache()
    }

    /**
     * Drops entries belonging to a server the user is no longer signed in to, and their cache copies.
     *
     * Resuming into another server's session is resuming into someone else's file; a cache copy with no
     * entry to explain it is a multi-gigabyte file nobody can claim.
     */
    fun forgetOtherServers() {
        val key = serverKey() ?: return
        val removed = journal.keepServer(key)
        for (entry in removed) {
            stager.discardCache(entry.sourceKey)
        }
        trimOrphanedCache()
        refreshResumable()
    }

    /** Removes any cache copy no live entry owns. */
    fun trimOrphanedCache() {
        val key = serverKey() ?: return
        stager.trim(journal.entries(key).mapTo(HashSet()) { it.sourceKey })
    }

    private fun launch(targetDirectoryPath: String, document: PickedDocument, resume: UploadResumeEntry?) {
        if (job?.isActive == true) return
        shownBytes = 0L
        activeUploadId = null
        stateFlow.value = UploadState(
            fileName = document.displayName,
            stage = UploadStage.Preparing,
            confirmedBytes = 0,
            inFlightBytes = 0,
            // A resume already knows the length; a fresh pick may not, until the provider is asked.
            totalBytes = resume?.totalLength,
            resumed = resume != null,
        )
        job = scope.launch(dispatcher) { execute(targetDirectoryPath, document, resume) }
    }

    private suspend fun execute(targetDirectoryPath: String, document: PickedDocument, resume: UploadResumeEntry?) {
        var uploadId: String? = null
        try {
            val staged = stager.stage(document) {
                // The provider had to copy the document into the cache: still nothing has left the device.
                stateFlow.value = stateFlow.value?.copy(stage = UploadStage.Preparing, totalBytes = null)
            }
            val source = staged.source
            val length = source.length
            val key = serverKey()
            if (key == null) {
                failWith(source.displayName, UploadFailure.SessionLost)
                return
            }
            stateFlow.value = stateFlow.value?.copy(fileName = source.displayName, totalBytes = length)

            // A remembered session is asked about first, and only a session the server confirms is gone
            // falls through to a fresh one: a request that simply failed answers nothing about whether the
            // old session still exists.
            val adopted = when (val adoption = resume?.let { adoptResume(it) } ?: Adoption.Gone) {
                is Adoption.Adopted -> adoption.session
                Adoption.Gone -> createSession(targetDirectoryPath, document, length) ?: return
                Adoption.Failed -> return
            }
            uploadId = adopted.uploadId
            activeUploadId = adopted.uploadId
            recordEntry(key, targetDirectoryPath, document, length, adopted.uploadId, adopted.offset)
            stateFlow.value = stateFlow.value?.copy(stage = UploadStage.Uploading, uploadId = adopted.uploadId)

            // The commit phase can send more data (an incomplete session answers with a lower offset), so
            // pumping and committing share one session state rather than restarting the arithmetic. The
            // round budget bounds the pair: a server that keeps refusing a commit while accepting the
            // bytes it still claims to be missing would otherwise be a livelock.
            var commitRounds = 0
            while (true) {
                if (adopted.offset < length && !pump(source, length, adopted, key, targetDirectoryPath, document)) {
                    return
                }
                stateFlow.value = stateFlow.value?.copy(stage = UploadStage.Committing, inFlightBytes = 0)
                when (val committed = commit(adopted.uploadId)) {
                    CommitOutcome.Done -> {
                        journal.remove(adopted.uploadId)?.let { stager.discardCache(it.sourceKey) }
                        activeUploadId = null
                        stateFlow.value = stateFlow.value?.copy(
                            stage = UploadStage.Finished,
                            confirmedBytes = length,
                            inFlightBytes = 0,
                        )
                        refreshResumable()
                        return
                    }

                    CommitOutcome.Resynchronise -> {
                        commitRounds++
                        if (commitRounds >= MAXIMUM_COMMIT_FAILURES) {
                            failWith(document.displayName, UploadFailure.Server)
                            return
                        }
                        // The refusal named no offset, so the truth has to be read: the local belief is
                        // exactly what the refusal just contradicted.
                        if (!resynchronise(adopted, null)) {
                            failWith(document.displayName, UploadFailure.Network)
                            return
                        }
                        if (adopted.offset >= length) {
                            // The server holds every byte and still refused: report it rather than loop.
                            failWith(document.displayName, UploadFailure.Server)
                            return
                        }
                        stateFlow.value = stateFlow.value?.copy(stage = UploadStage.Uploading)
                    }

                    is CommitOutcome.Failed -> {
                        failWith(document.displayName, committed.failure)
                        return
                    }
                }
            }
        } catch (cancelled: CancellationException) {
            // Cancel is handled by cancel(): it owns the abandon. A cancellation from anywhere else
            // (process shutdown) must still leave the session and the entry intact for a later resume.
            stateFlow.value = null
            refreshResumable()
            throw cancelled
        } catch (error: InsufficientCacheSpaceException) {
            failWith(document.displayName, UploadFailure.LocalCache)
        } catch (error: SeekUnsupportedException) {
            failWith(document.displayName, UploadFailure.SourceUnreadable)
        } catch (error: Exception) {
            failWith(document.displayName, UploadFailure.Network)
        } finally {
            activeUploadId = null
            job = null
        }
    }

    /**
     * The session the coordinator appends to, and the numbers it carries between phases.
     *
     * [ceiling] is the server's advertised chunk size and is never exceeded: the server sizes its request
     * body limit to exactly that number, so a larger chunk is refused as `upload-chunk-too-large`. The
     * floor for shrinking is therefore the *smaller* of our preference and the server's ceiling — halving
     * a chunk that is already too large would keep being refused until the retry budget ran out.
     */
    private class ActiveSession(uploadId: String, offset: Long, ceiling: Int) {
        val uploadId: String = uploadId
        var offset: Long = offset

        /** Never zero or negative, however the server answered. */
        val ceiling: Int = ceiling

        private val floor: Int = minOf(MINIMUM_CHUNK_SIZE, this.ceiling)

        var chunkSize: Int = this.ceiling
        var failures: Int = 0

        /** When the resume entry was last written, so per-chunk journal writes cannot flood the disk. */
        var recordedAtMillis: Long = 0L

        fun shouldRecord(nowMillis: Long): Boolean = nowMillis - recordedAtMillis >= RECORD_INTERVAL_MILLIS

        /** A smaller chunk retries more cheaply on a bad link, down to the floor and no further. */
        fun shrink() {
            chunkSize = maxOf(floor, chunkSize / 2)
        }

        /** Climbs back towards the ceiling after a success, so one bad patch does not slow the rest. */
        fun grow() {
            if (chunkSize < this.ceiling) chunkSize = minOf(this.ceiling, chunkSize * 2)
        }
    }

    /**
     * Re-opens the session a resume entry remembers.
     *
     * The three answers are deliberately kept apart, because collapsing them is how one file ends up
     * split across two sessions:
     *
     *  - [Adoption.Adopted] — the session is alive and its offset is the truth;
     *  - [Adoption.Gone] — the server no longer has it (expired, swept, or owned by another identity).
     *    The entry is dropped first so the dead session cannot be offered again, and a fresh one is opened;
     *  - [Adoption.Failed] — nothing was decided. A transport failure is *not* [Adoption.Gone]: opening a
     *    second session for the same document while the first may still be alive would leave a staging
     *    file nobody owns, so the transfer stops and the entry is kept for the next attempt.
     */
    private suspend fun adoptResume(entry: UploadResumeEntry): Adoption {
        val url = session.serverUrl
        val token = session.accessToken
        if (url == null || token == null) {
            failWith(entry.fileName, UploadFailure.SessionLost)
            return Adoption.Failed
        }
        return when (val result = gateway.uploadSession(url, token, entry.uploadId)) {
            is ApiResult.Success -> Adoption.Adopted(
                ActiveSession(
                    uploadId = result.value.uploadId,
                    offset = result.value.offset,
                    ceiling = result.value.chunkSize.coerceAtLeast(1),
                ),
            )

            is ApiResult.Problem -> {
                journal.remove(entry.uploadId)?.let { stager.discardCache(it.sourceKey) }
                refreshResumable()
                Adoption.Gone
            }

            is ApiResult.Transport -> {
                failWith(entry.fileName, UploadFailure.Network)
                Adoption.Failed
            }
        }
    }

    /** What asking the server about a remembered session established. */
    private sealed interface Adoption {
        data class Adopted(val session: ActiveSession) : Adoption

        data object Gone : Adoption

        data object Failed : Adoption
    }

    /**
     * Opens a fresh session.
     *
     * Elevation is settled here and only here: a protected destination answers `elevation-required` once,
     * the user is asked once, and the session then carries the decision. That is what keeps an
     * authorization dialog from appearing forty minutes into a transfer.
     */
    private suspend fun createSession(
        targetDirectoryPath: String,
        document: PickedDocument,
        length: Long,
    ): ActiveSession? {
        val result = elevations.withPathElevation(
            path = targetDirectoryPath,
            capability = FileElevationCapabilities.UPLOAD,
            includeDescendants = true,
            provider = elevationAnswers,
        ) { serverUrl, accessToken ->
            gateway.createUploadSession(
                serverUrl = serverUrl,
                accessToken = accessToken,
                targetDirectoryPath = targetDirectoryPath,
                fileName = document.displayName,
                length = length,
                lastModifiedMillis = document.lastModifiedMillis,
                // Retry-safe: the same key returns the same session, so a lost response cannot leak a
                // second staging file on the server.
                idempotencyKey = UUID.randomUUID().toString().replace("-", ""),
            )
        }
        return when (result) {
            is ApiResult.Success -> ActiveSession(
                uploadId = result.value.uploadId,
                // A fresh session may already hold bytes: the server answers with the truth, not with zero.
                offset = result.value.offset,
                ceiling = result.value.chunkSize.coerceAtLeast(1),
            )

            is ApiResult.Problem -> {
                failWith(document.displayName, failureFor(result.code, result.status))
                null
            }

            is ApiResult.Transport -> {
                failWith(document.displayName, UploadFailure.Network)
                null
            }
        }
    }

    /**
     * Sends chunks until the server holds the declared length.
     *
     * Returns false when the transfer gave up — [failWith] has already published why — and true when the
     * offset reached the target. Every exit leaves the journal holding the last offset the server
     * confirmed, because that number, not the local count, is what a later resume starts from.
     */
    private suspend fun pump(
        source: UploadSource,
        target: Long,
        active: ActiveSession,
        key: String,
        targetDirectoryPath: String,
        document: PickedDocument,
    ): Boolean {
        while (active.offset < target) {
            val url = session.serverUrl
            val token = session.accessToken
            if (url == null || token == null) {
                failWith(source.displayName, UploadFailure.SessionLost)
                return false
            }
            val length = minOf(active.chunkSize.toLong(), target - active.offset)
            val result = try {
                source.openAt(active.offset).use { stream ->
                    gateway.sendUploadChunk(
                        serverUrl = url,
                        accessToken = token,
                        uploadId = active.uploadId,
                        offset = active.offset,
                        chunkLength = length,
                        source = stream,
                    ) { inFlight -> publish(active.offset, inFlight) }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: SeekUnsupportedException) {
                failWith(source.displayName, UploadFailure.SourceUnreadable)
                return false
            } catch (error: Exception) {
                UploadChunkResult.Unreachable(error.message)
            }

            when (result) {
                is UploadChunkResult.Confirmed -> {
                    if (result.offset <= active.offset) {
                        // The server named no new byte. Counting a chunk it cannot prove arrived is the one
                        // thing resumption exists to prevent, so this resynchronises — and shares the retry
                        // budget, because a server that keeps answering the same number would otherwise spin
                        // here forever.
                        active.failures++
                        if (active.failures >= MAXIMUM_CHUNK_FAILURES) {
                            failWith(source.displayName, UploadFailure.Network)
                            return false
                        }
                        val wait = retryDelayMillis(active.failures)
                        if (wait > 0L) delay(wait)
                        if (!resynchronise(active, null)) {
                            failWith(source.displayName, UploadFailure.Network)
                            return false
                        }
                        continue
                    }
                    active.offset = result.offset
                    active.failures = 0
                    active.grow()
                    publish(active.offset, 0L)
                    // Throttled: the entry only has to be close enough to offer "continue", because a
                    // resume asks the server for the authoritative offset rather than trusting this one.
                    val now = System.currentTimeMillis()
                    if (active.shouldRecord(now)) {
                        active.recordedAtMillis = now
                        recordEntry(key, targetDirectoryPath, document, target, active.uploadId, active.offset)
                    }
                }

                is UploadChunkResult.SourceShort -> {
                    failWith(source.displayName, UploadFailure.SourceChanged)
                    return false
                }

                is UploadChunkResult.Refused -> when {
                    result.status == 401 -> {
                        if (!renewToken()) {
                            failWith(source.displayName, UploadFailure.SessionLost)
                            return false
                        }
                        if (!resynchronise(active, result.offset)) {
                            failWith(source.displayName, UploadFailure.Network)
                            return false
                        }
                    }

                    UploadProblemCodes.isSessionLost(result.code) -> {
                        failWith(source.displayName, UploadFailure.SessionLost)
                        return false
                    }

                    UploadProblemCodes.canContinue(result.code) -> {
                        // A normal answer, not a failure: the server is saying where it actually is.
                        val before = active.offset
                        if (!resynchronise(active, result.offset)) {
                            failWith(source.displayName, UploadFailure.Network)
                            return false
                        }
                        if (result.code == UploadProblemCodes.CHUNK_TOO_LARGE) active.shrink()
                        // A refusal that leaves the offset no further along is a stall however it is named,
                        // and it shares the budget: a server that keeps answering the same thing would
                        // otherwise spin here forever, because every one of these answers is "not a failure".
                        if (active.offset <= before) {
                            active.failures++
                            if (active.failures >= MAXIMUM_CHUNK_FAILURES) {
                                failWith(source.displayName, UploadFailure.Network)
                                return false
                            }
                            val wait = retryDelayMillis(active.failures)
                            if (wait > 0L) delay(wait)
                        }
                    }

                    else -> {
                        failWith(source.displayName, failureFor(result.code, result.status))
                        return false
                    }
                }

                is UploadChunkResult.Unreachable -> {
                    active.failures++
                    if (active.failures >= MAXIMUM_CHUNK_FAILURES) {
                        // The session and the entry stay: the user's next attempt continues from here.
                        failWith(source.displayName, UploadFailure.Network)
                        return false
                    }
                    active.shrink()
                    val wait = retryDelayMillis(active.failures)
                    if (wait > 0L) delay(wait)
                    if (!resynchronise(active, null)) {
                        failWith(source.displayName, UploadFailure.Network)
                        return false
                    }
                    recordEntry(key, targetDirectoryPath, document, target, active.uploadId, active.offset)
                }
            }
        }
        return true
    }

    /**
     * Adopts the authoritative offset after any doubt about what the server received.
     *
     * [hint] is the offset a refusal already carried, which is what lets a resynchronisation cost no extra
     * round trip; `null` means the server said nothing this time and has to be asked. The card is told
     * that a check is running, because during the round trip there is no number worth displaying.
     *
     * Returns false when the server could not be asked at all. That is a stop, not a resumption: the
     * local estimate is not evidence, and continuing from it is exactly the assumption resumption exists
     * to remove.
     */
    private suspend fun resynchronise(active: ActiveSession, hint: Long?): Boolean {
        if (hint != null) {
            active.offset = hint
            return true
        }
        stateFlow.value = stateFlow.value?.copy(resynchronising = true)
        val resolved = resolveOffset(active.uploadId)
        stateFlow.value = stateFlow.value?.copy(resynchronising = false)
        if (resolved == null) return false
        active.offset = resolved
        return true
    }

    /** The server's own number, or `null` when it cannot be asked. Never a local guess. */
    private suspend fun resolveOffset(uploadId: String): Long? {
        val url = session.serverUrl ?: return null
        val token = session.accessToken ?: return null
        return when (val result = gateway.uploadSession(url, token, uploadId)) {
            is ApiResult.Success -> result.value.offset
            // Unreadable for now. Reporting the local estimate here would be the one thing the whole
            // design forbids, so "no answer" stays distinguishable from "an answer".
            else -> null
        }
    }

    private suspend fun renewToken(): Boolean = session.renew() is ApiResult.Success

    private sealed interface CommitOutcome {
        data object Done : CommitOutcome

        /**
         * The server refused because it holds fewer bytes than the client believed.
         *
         * Carries no offset on purpose: the refusal is precisely the statement that the client's belief
         * was wrong, so the caller reads the truth from the server rather than being handed a number the
         * commit path would have had to guess.
         */
        data object Resynchronise : CommitOutcome

        data class Failed(val failure: UploadFailure) : CommitOutcome
    }

    private suspend fun commit(uploadId: String): CommitOutcome {
        var attempts = 0
        while (true) {
            val url = session.serverUrl
            val token = session.accessToken
            if (url == null || token == null) return CommitOutcome.Failed(UploadFailure.SessionLost)
            when (val result = gateway.commitUpload(url, token, uploadId)) {
                is ApiResult.Success -> return CommitOutcome.Done

                is ApiResult.Problem -> when {
                    result.code == UploadProblemCodes.INCOMPLETE || result.code == UploadProblemCodes.OFFSET_MISMATCH ->
                        return CommitOutcome.Resynchronise

                    result.isSessionExpired() -> {
                        if (!renewToken()) return CommitOutcome.Failed(UploadFailure.SessionLost)
                    }

                    else -> return CommitOutcome.Failed(failureFor(result.code, result.status))
                }

                is ApiResult.Transport -> {
                    attempts++
                    if (attempts >= MAXIMUM_COMMIT_FAILURES) return CommitOutcome.Failed(UploadFailure.Network)
                    val wait = retryDelayMillis(attempts)
                    if (wait > 0L) delay(wait)
                    // The session is kept, so the retry continues instead of sending the file again.
                }
            }
        }
    }

    /** Publishes progress, never below what the card has already shown. */
    private fun publish(confirmed: Long, inFlight: Long) {
        val current = stateFlow.value ?: return
        val total = current.totalBytes
        val value = total?.let { minOf(confirmed + inFlight, it) } ?: (confirmed + inFlight)
        shownBytes = maxOf(shownBytes, value)
        stateFlow.value = current.copy(
            confirmedBytes = confirmed,
            inFlightBytes = maxOf(0L, shownBytes - confirmed),
        )
    }

    private fun recordEntry(
        key: String,
        targetDirectoryPath: String,
        document: PickedDocument,
        length: Long,
        uploadId: String,
        confirmed: Long,
    ) {
        journal.record(
            UploadResumeEntry(
                serverKey = key,
                uploadId = uploadId,
                targetDirectoryPath = targetDirectoryPath,
                fileName = document.displayName,
                sourceUri = document.uri,
                sourceKey = document.sourceKey,
                confirmedOffset = confirmed,
                totalLength = length,
                recordedAtMillis = System.currentTimeMillis(),
            ),
        )
        refreshResumable()
    }

    private suspend fun abortSession(uploadId: String) {
        val url = session.serverUrl ?: return
        val token = session.accessToken ?: return
        gateway.abortUpload(url, token, uploadId)
    }

    private fun refreshResumable() {
        val key = serverKey()
        resumableFlow.value = if (key == null) emptyList() else journal.entries(key)
    }

    private fun failWith(fileName: String, failure: UploadFailure) {
        val current = stateFlow.value
        stateFlow.value = UploadState(
            fileName = current?.fileName ?: fileName,
            stage = current?.stage ?: UploadStage.Uploading,
            // Only bytes the server confirmed: the in-flight part was never proven to have arrived, and a
            // stopped card that claimed it would promise progress a resume does not have.
            confirmedBytes = current?.confirmedBytes ?: 0L,
            inFlightBytes = 0L,
            totalBytes = current?.totalBytes,
            // Carried through: the card looks up the resume entry by session id to offer "continue", so
            // dropping it here would leave a continuable upload with nothing but a dismiss button.
            uploadId = current?.uploadId,
            resumed = current?.resumed ?: false,
            resynchronising = false,
            failure = failure,
        )
        // The pairing rule, applied from the other side: an entry is deleted exactly when the session it
        // explains can no longer be used — never merely because a transfer stopped.
        val dead = current?.uploadId?.takeIf { !failure.keepsResumeEntry }
        if (dead != null) {
            journal.remove(dead)?.let { stager.discardCache(it.sourceKey) }
        }
        refreshResumable()
    }

    private fun failureFor(code: String, status: Int): UploadFailure = when {
        code == UploadProblemCodes.INSUFFICIENT_STORAGE || status == 507 -> UploadFailure.ServerStorage
        code == ProblemCodes.ELEVATION_REQUIRED || status == 403 -> UploadFailure.ElevationRequired
        code == UploadProblemCodes.INVALID_FILE_NAME -> UploadFailure.NameUnusable
        UploadProblemCodes.isSessionLost(code) -> UploadFailure.SessionLost
        else -> UploadFailure.Server
    }

    companion object {
        /** Chunk attempts allowed before the transfer is reported as interrupted (the session is kept). */
        const val MAXIMUM_CHUNK_FAILURES = 10

        /** Commit attempts allowed before the same verdict. */
        const val MAXIMUM_COMMIT_FAILURES = 5

        /** Chunk size floor after repeated failures: smaller chunks retry more cheaply on a bad link. */
        const val MINIMUM_CHUNK_SIZE = 1024 * 1024

        /** Shortest gap between two resume-journal writes during a transfer. */
        const val RECORD_INTERVAL_MILLIS = 2_000L

        /** Exponential backoff with jitter, so many clients on one server do not retry in lockstep. */
        fun defaultRetryDelayMillis(failures: Int): Long {
            val seconds = minOf(30L, 1L shl minOf(failures, 5))
            val jitter = ThreadLocalRandom.current().nextDouble(0.875, 1.125)
            return (seconds * 1000.0 * jitter).toLong()
        }
    }
}
