package app.relaxkonos.mobile.core.net

import java.util.UUID
import org.json.JSONObject

/** Current InstallationContracts projection. Route spelling follows InstallationApiRoutes. */
enum class InstallationService(val wire: String, val capability: String, val elevation: String) {
    Smb("smb", ServerCapabilities.FILE_SERVICES, "smbInstall"),
    Nginx("nginx", ServerCapabilities.WEB_SERVER, "nginxInstall"),
    Frp("frp", ServerCapabilities.TUNNELS, "frpInstall"),
    Mihomo("mihomo", ServerCapabilities.PROXY, "mihomoInstall"),
    Docker("docker", ServerCapabilities.DOCKER, "dockerInstall"),
    Git("git", ServerCapabilities.GIT, "gitPackageInstall");

    val kinds: Set<InstallationKind> get() = when (this) {
        Smb, Docker, Git -> setOf(InstallationKind.Install)
        else -> InstallationKind.entries.toSet()
    }
    val acceptsPackage: Boolean get() = this == Nginx || this == Frp || this == Mihomo
    val supportsRollback: Boolean get() = this == Frp || this == Mihomo
}

enum class InstallationKind(val wire: String) {
    Install("install"), Upgrade("upgrade"), Repair("repair"), Uninstall("uninstall")
}

enum class InstallationState(val wire: String) {
    Queued("queued"), Running("running"), Succeeded("succeeded"), Failed("failed"),
    Cancelled("cancelled"), Interrupted("interrupted");
    val active: Boolean get() = this == Queued || this == Running
}

enum class InstallationStage(val wire: String) {
    Queued("queued"), Preflight("preflight"), Preparing("preparing"), UpdatingPackageLists("updatingPackageLists"),
    Downloading("downloading"), Copying("copying"), Verifying("verifying"), Extracting("extracting"),
    Installing("installing"), Configuring("configuring"), Activating("activating"), HealthChecking("healthChecking"),
    RollingBack("rollingBack"), Completed("completed"), Failed("failed"), Cancelled("cancelled"), Interrupted("interrupted")
}

object InstallationProblemCodes {
    const val STORE_UNAVAILABLE = "installation.store_unavailable"
    const val INVALID_REQUEST = "installation.invalid_request"
    const val CONFIRMATION_REQUIRED = "installation.confirmation_required"
    const val IDEMPOTENCY_REQUIRED = "installation.idempotency_required"
    const val IDEMPOTENCY_CONFLICT = "installation.idempotency_conflict"
    const val RESOURCE_CONFLICT = "installation.resource_conflict"
    const val NOT_CANCELLABLE = "installation.not_cancellable"
    const val INTERRUPTED = "installation.interrupted"
    const val CANCELLED = "installation.cancelled"
    const val HELPER_PROTOCOL = "installation.helper_protocol"
    const val NOT_SUPPORTED = "installation.not_supported"
    const val FAILED = "installation.failed"
    const val RECOVERY_UNKNOWN = "installation.recovery_unknown"
    const val HEALTH_CHECK_FAILED = "installation.health_check_failed"
    const val FILE_REFERENCE_UNAVAILABLE = "installation.file_reference_unavailable"
    const val PERMISSION_DENIED = "installation.permission_denied"
}

data class InstallationOperation(
    val operationId: String,
    val service: InstallationService,
    val kind: InstallationKind,
    val state: InstallationState,
    val stage: InstallationStage,
    /** Verified progress in this stage, never overall progress. */
    val progress: Int?,
    val problemCode: String?,
    val createdAtMillis: Long,
    val startedAtMillis: Long?,
    val completedAtMillis: Long?,
    val cancellable: Boolean,
)

data class InstallationFileReference(val id: String, val fileName: String, val length: Long, val expiresAtMillis: Long) {
    fun expired(nowMillis: Long = System.currentTimeMillis()): Boolean = expiresAtMillis <= nowMillis
}

sealed interface InstallationRequest {
    val confirmed: Boolean
    val service: InstallationService
}
data class SmbInstallationRequest(override val confirmed: Boolean) : InstallationRequest { override val service = InstallationService.Smb }
data class GitInstallationRequest(override val confirmed: Boolean) : InstallationRequest { override val service = InstallationService.Git }
data class DockerInstallationRequest(override val confirmed: Boolean) : InstallationRequest { override val service = InstallationService.Docker }
data class NginxInstallationRequest(override val confirmed: Boolean, val version: String? = null, val fileReferenceId: String? = null) : InstallationRequest {
    override val service = InstallationService.Nginx
}
data class FrpInstallationRequest(override val confirmed: Boolean, val version: String? = null, val rollback: Boolean = false,
    val fileReferenceId: String? = null) : InstallationRequest { override val service = InstallationService.Frp }
data class MihomoInstallationRequest(override val confirmed: Boolean, val version: String? = null, val rollback: Boolean = false,
    val fileReferenceId: String? = null) : InstallationRequest { override val service = InstallationService.Mihomo }

object InstallationRoutes {
    const val ROOT = "/api/v1.0/installations"
    fun start(service: InstallationService, kind: InstallationKind) = "$ROOT/${service.name}/${kind.name}"
    fun operation(id: String) = "$ROOT/${canonicalId(id)}"
    fun cancel(id: String) = "${operation(id)}/cancel"
    fun active(service: InstallationService) = "$ROOT/active?service=${service.name}"
    fun fileReference(service: InstallationService) = "$ROOT/${service.name}/file-reference"
    fun packageUpload(service: InstallationService) = "$ROOT/${service.name}/package"
    internal fun canonicalId(id: String): String {
        require(id.matches(Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")))
        return UUID.fromString(id).toString()
    }
}

internal object InstallationWire {
    fun request(request: InstallationRequest, kind: InstallationKind): JsonBody {
        require(kind in request.service.kinds)
        val body = JsonBody().bool("confirmed", request.confirmed)
        when (request) {
            is NginxInstallationRequest -> {
                validateSource(kind, request.fileReferenceId, false)
                body.string("version", request.version).string("fileReferenceId", request.fileReferenceId)
            }
            is FrpInstallationRequest -> {
                validateSource(kind, request.fileReferenceId, request.rollback)
                body.string("version", request.version).bool("rollback", request.rollback).string("fileReferenceId", request.fileReferenceId)
            }
            is MihomoInstallationRequest -> {
                validateSource(kind, request.fileReferenceId, request.rollback)
                body.string("version", request.version).bool("rollback", request.rollback).string("fileReferenceId", request.fileReferenceId)
            }
            else -> Unit
        }
        return body
    }

    private fun validateSource(kind: InstallationKind, reference: String?, rollback: Boolean) {
        require(!rollback || kind == InstallationKind.Repair)
        require(reference == null || (reference.isNotBlank() && kind == InstallationKind.Install && !rollback))
    }

    fun operation(payload: String): InstallationOperation = JSONObject(payload).let { json ->
        InstallationOperation(
            InstallationRoutes.canonicalId(json.getString("operationId")),
            InstallationService.entries.single { it.wire == json.getString("service") },
            InstallationKind.entries.single { it.wire == json.getString("kind") },
            InstallationState.entries.single { it.wire == json.getString("state") },
            InstallationStage.entries.single { it.wire == json.getString("stage") },
            if (json.isNull("progress")) null else json.getInt("progress").also { require(it in 0..100) },
            json.optNullableString("problemCode"), instant(json, "createdAt"),
            optionalInstant(json, "startedAt"), optionalInstant(json, "completedAt"), json.getBoolean("cancellable"),
        )
    }

    fun fileReference(payload: String): InstallationFileReference = JSONObject(payload).let { json ->
        InstallationFileReference(json.getString("id").also { require(it.isNotBlank()) }, json.getString("fileName"),
            json.getLong("length").also { require(it >= 0) }, instant(json, "expiresAt"))
    }
    private fun instant(json: JSONObject, key: String): Long = requireNotNull(IsoInstant.toEpochMillis(json.getString(key)))
    private fun optionalInstant(json: JSONObject, key: String): Long? = if (json.isNull(key)) null else instant(json, key)
}
