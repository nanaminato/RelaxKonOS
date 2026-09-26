package app.relaxkonos.mobile.core.net

/**
 * Client-side models of the RelaxKonOS wire contract. Field names and enum values must stay in sync
 * with `RelaxKonOS.Protocol`; enums travel as camelCase strings (`RelaxKonOSJsonOptions.Default`).
 */
data class AuthTokens(
    val accessToken: String,
    val refreshToken: String,
    val accessTokenExpiresAtMillis: Long?,
    val refreshTokenExpiresAtMillis: Long?,
)

data class ServerDescriptor(val platform: String, val capabilities: Set<String>)

/**
 * Whether the identity that just signed in may run ordinary operations (files, terminal, Git) on the
 * host. A per-login fact, not a deployment fact: the same server answers differently for root than for
 * a regular account. When [available] is false the shell must say so instead of letting the first
 * folder open fail with a 503.
 *
 * [reason] is a stable kebab-case code ([ExecutionEligibilityReasons]), never a sentence: the text
 * belongs to `strings.xml`, and this class never renders it.
 */
data class ExecutionEligibility(val available: Boolean, val reason: String?, val privilegedFilesAvailable: Boolean) {
    companion object {
        /**
         * What a session reads as when the server did not state it — a synthesised session in a test or
         * a preview. Every wire parse sets the field explicitly, so this is never a substitute for the
         * server's answer on a real connection.
         */
        val Available = ExecutionEligibility(available = true, reason = null, privilegedFilesAvailable = false)
    }
}

/** Stable reason codes for [ExecutionEligibility.reason], mirroring `ServerExecutionEligibilityReasons`. */
object ExecutionEligibilityReasons {
    const val RESERVED_IDENTITY = "reserved-identity"
    const val SYSTEM_ACCOUNT = "system-account"
    const val UNVERIFIED_HOME_DIRECTORY = "unverified-home-directory"
    const val SERVER_ACCOUNT_REQUIRED = "server-account-required"
    const val WINDOWS_PROFILE_REQUIRED = "windows-profile-required"
    const val UNSUPPORTED_PLATFORM = "unsupported-platform"
}

data class LoginSession(
    val userName: String,
    val workspaceName: String,
    val server: ServerDescriptor,
    val tokens: AuthTokens,
    val executionEligibility: ExecutionEligibility = ExecutionEligibility.Available,
)

/** `HostElevationResult` / result of the one-shot elevation call. */
data class ElevationGrant(val elevated: Boolean, val expiresAtMillis: Long?)

/** `FileElevationResult`: result of testing or granting elevated access for a file path. */
data class FileElevationGrant(
    val requiresElevation: Boolean,
    val elevated: Boolean,
    val expiresAtMillis: Long?,
)

/** `FileElevationCapability` values, sent as the request `capability` field. */
object FileElevationCapabilities {
    const val READ = "read"
    const val WRITE = "write"
    const val CREATE_DIRECTORY = "createDirectory"
    const val DELETE = "delete"
    const val RENAME = "rename"
    const val MOVE = "move"
    const val COPY = "copy"
    const val UPLOAD = "upload"
}

/** One entry of a directory listing. */
data class RemoteEntry(
    val path: String,
    val name: String,
    val isDirectory: Boolean,
    val sizeBytes: Long?,
    val modifiedAtMillis: Long?,
    val mimeType: String?,
)

data class DirectoryListing(val path: String, val name: String, val entries: List<RemoteEntry>)

data class RemoteFileProperties(
    val path: String,
    val name: String,
    val isDirectory: Boolean,
    val sizeBytes: Long?,
    val createdMillis: Long?,
    val modifiedMillis: Long?,
    val permissions: String,
    val unixMode: Int?,
)

data class DiskUsage(val id: String, val usedBytes: Long, val totalBytes: Long, val percent: Double)

data class PerformanceSnapshot(
    val cpuPercent: Double,
    val memoryUsedBytes: Long,
    val memoryTotalBytes: Long,
    val uptimeSeconds: Long,
    val filesystems: List<DiskUsage>,
    val isStale: Boolean,
    val lastSampleMillis: Long?,
)

data class RemoteProcess(
    val pid: Int,
    val name: String,
    val cpuPercent: Double,
    val memoryBytes: Long,
    val userName: String?,
    val threadCount: Int,
)

data class ProcessPage(val items: List<RemoteProcess>, val totalCount: Int)

/**
 * State of one resumable upload session, mirroring `UploadSessionDto`.
 *
 * [offset] is the single source of truth for resumption. A client that is unsure what the server
 * received asks for it rather than inferring an answer from what it believes it sent, which is the
 * rule the whole resumable design rests on.
 */
data class UploadSession(
    val uploadId: String,
    val offset: Long,
    val length: Long,
    val chunkSize: Int,
    val elevated: Boolean,
    val expiresAtMillis: Long?,
)

/**
 * Fixed numbers of the resumable upload protocol, mirroring `FileUploadProtocol`.
 *
 * They live here so the client cannot invent a limit of its own: the server enforces the same values
 * and answers with a named problem when one is exceeded.
 */
object UploadProtocol {
    const val DEFAULT_CHUNK_SIZE = 8 * 1024 * 1024

    /** Ceiling of the single-shot route. */
    const val SINGLE_SHOT_MAXIMUM_BYTES = 16L * 1024 * 1024

    /**
     * Client-side dispatch threshold: at or below this a file still travels in one request.
     *
     * A scheduling choice, not a contract — the server never depends on it. It exists so the common
     * case (a photo, a PDF) stays one request instead of paying for a session it does not need.
     */
    const val SINGLE_SHOT_THRESHOLD_BYTES = 4L * 1024 * 1024

    /** Carries the authoritative byte offset, on a chunk request and on a conflict answer. */
    const val OFFSET_HEADER = "Upload-Offset"

    /** Makes session creation retry-safe; required by the server. */
    const val IDEMPOTENCY_KEY_HEADER = "Idempotency-Key"
}

/**
 * Stable problem codes of the upload surface, mirroring `FileUploadProblemCodes`. Clients branch on
 * these; a raw I/O message is never a product message.
 */
object UploadProblemCodes {
    const val INVALID_FILE_NAME = "invalid-file-name"
    const val INSUFFICIENT_STORAGE = "insufficient-storage"
    const val TOO_MANY_UPLOADS = "too-many-uploads"
    const val IDEMPOTENCY_CONFLICT = "idempotency-conflict"
    const val IDEMPOTENCY_REQUIRED = "idempotency-required"
    const val SESSION_NOT_FOUND = "upload-session-not-found"
    const val SESSION_EXPIRED = "upload-session-expired"
    const val OFFSET_MISMATCH = "upload-offset-mismatch"
    const val CONCURRENT_CHUNK = "upload-concurrent-chunk"
    const val LENGTH_EXCEEDED = "upload-length-exceeded"
    const val CHUNK_TOO_LARGE = "upload-chunk-too-large"
    const val LENGTH_REQUIRED = "length-required"
    const val INCOMPLETE = "upload-incomplete"
    const val HASH_MISMATCH = "upload-hash-mismatch"
    const val TOO_LARGE_FOR_SINGLE_SHOT = "upload-too-large-for-single-shot"

    /** The session is gone or expired, so the only correct action is a fresh start. */
    fun isSessionLost(code: String): Boolean = code == SESSION_NOT_FOUND || code == SESSION_EXPIRED

    /**
     * True when the answer means "resynchronise and keep going" rather than "give up": the server is
     * telling the client where it actually is. `offset-mismatch` has the same standing as
     * `thumbnail-unsupported` does on the preview path — a normal reply, not a failure.
     */
    fun canContinue(code: String): Boolean = code == OFFSET_MISMATCH || code == CONCURRENT_CHUNK ||
        code == CHUNK_TOO_LARGE || code == LENGTH_REQUIRED
}

/**
 * Outcome of sending one chunk.
 *
 * A chunk answer is deliberately not an [ApiResult]: the two facts the loop needs — whether the server
 * gave a verdict at all, and the offset it actually holds — are both absent from the generic shape. A
 * refusal carries the authoritative offset as a hint so a resynchronisation costs no extra round trip;
 * a `null` there means "ask with GET".
 */
sealed interface UploadChunkResult {
    /** The server appended the chunk and holds [offset] bytes. */
    data class Confirmed(val offset: Long) : UploadChunkResult

    data class Refused(val status: Int, val code: String, val offset: Long?) : UploadChunkResult

    /**
     * The source could not deliver the declared bytes, so the document changed while it was being sent.
     * Retrying cannot fix this and the session must not be resumed into: publishing half of the old file
     * and half of the new one is worse than failing.
     */
    data class SourceShort(val deliveredBytes: Long) : UploadChunkResult

    /** No verdict: no network, a timeout, or a 5xx that named no RelaxKonOS problem code. */
    data class Unreachable(val detail: String?) : UploadChunkResult
}

/** Raised inside the chunk sender when the source stops before the declared length. */
internal class TruncatedSourceException(val deliveredBytes: Long) : Exception("source delivered $deliveredBytes bytes")

/**
 * Stable server capability identifiers, mirroring `ServerCapabilities` in `RelaxKonOS.Protocol`.
 * Navigation entries are gated on these; a missing capability means the entry does not exist rather
 * than appearing disabled.
 */
object ServerCapabilities {
    const val FILES = "server.files"
    const val METRICS = "server.metrics"
    const val PROCESSES = "server.processes"
    const val TERMINAL = "server.terminal"
    const val POSIX_PERMISSIONS = "server.posix.permissions"
    const val GUARDIAN = "server.guardian"
    const val DOCKER = "server.docker"
    const val APPLICATION_DEPLOYMENTS = "server.application-deployments"
    const val WEB_SERVER = "server.web-server"
}
