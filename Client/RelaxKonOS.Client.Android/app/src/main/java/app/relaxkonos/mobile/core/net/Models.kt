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

data class LoginSession(
    val userName: String,
    val workspaceName: String,
    val server: ServerDescriptor,
    val tokens: AuthTokens,
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
