package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

/** Android projection of the Docker REST contract. YAML execution remains entirely server-owned. */
data class DockerStatus(val available: Boolean, val problemCode: String, val serverVersion: String, val operatingSystem: String, val architecture: String)
data class DockerContainer(val id: String, val names: String, val image: String, val state: String, val status: String)
data class DockerImage(val id: String, val repository: String, val tag: String, val size: String, val createdSince: String)
data class DockerNetwork(val id: String, val name: String, val driver: String, val scope: String)
data class DockerVolume(val name: String, val driver: String, val mountpoint: String)
/** [usedBy] lists containers — running or stopped — that still reference the volume. The server
 * refuses to delete an in-use volume, so this is the impact shown before a deletion is attempted. */
data class DockerVolumeDetails(val name: String, val driver: String, val mountpoint: String, val usedBy: List<String>)
data class DockerStack(val name: String, val status: String, val configFiles: String, val configDirectory: String)
data class DockerStackService(val service: String, val container: String, val image: String, val state: String, val status: String)
data class DockerLogs(val lines: List<String>, val truncated: Boolean)
data class DockerOperation(val success: Boolean, val problemCode: String, val messages: List<String>)

/** What the server's Compose parser resolved for a definition. Nothing has been applied. */
data class DockerStackPreviewService(val service: String, val image: String, val ports: List<String>)
data class DockerStackPreview(
    val projectName: String,
    val definitionVersion: String,
    val services: List<DockerStackPreviewService>,
    val volumes: List<String>,
    val networks: List<String>,
)

/**
 * Lifecycle of one durable stack operation. An unrecognised value from a newer server is kept as
 * [UNKNOWN] instead of crashing the parser or being shown as a state it is not.
 */
enum class DockerStackOperationKind(val wire: String) {
    DEPLOY("deploy"), START("start"), STOP("stop"), RESTART("restart"), DELETE("delete"), UNKNOWN("");

    companion object {
        fun fromWire(value: String): DockerStackOperationKind = entries.firstOrNull { it.wire == value } ?: UNKNOWN
    }
}

/**
 * [PARTIAL_FAILED] and [INTERRUPTED] are terminal but do not mean "nothing happened": the first means
 * some services are not in their desired state, and the second means the server restarted while the
 * operation was active and the project was observed rather than replayed.
 */
enum class DockerStackOperationState(val wire: String) {
    QUEUED("queued"), RUNNING("running"), SUCCEEDED("succeeded"), PARTIAL_FAILED("partialFailed"),
    FAILED("failed"), CANCELLED("cancelled"), INTERRUPTED("interrupted"), UNKNOWN("");

    /** True while the server may still change the outcome. */
    val active: Boolean get() = this == QUEUED || this == RUNNING

    /** True once the caller has to decide whether to retry, stop, or remove the project. */
    val needsAttention: Boolean get() = this == PARTIAL_FAILED || this == INTERRUPTED || this == FAILED

    companion object {
        fun fromWire(value: String): DockerStackOperationState = entries.firstOrNull { it.wire == value } ?: UNKNOWN
    }
}

/**
 * A durable Compose operation. It is the authoritative answer to "what happened to my stack", so a
 * client that lost its connection reads this record instead of guessing from a broken request.
 */
data class DockerStackOperation(
    val operationId: String,
    val projectName: String,
    val kind: DockerStackOperationKind,
    val state: DockerStackOperationState,
    val stage: String,
    val problemCode: String?,
    val recoveryProblemCode: String?,
    val createdAt: String,
    val cancellable: Boolean,
    val services: List<DockerStackService>,
)

/** Bounded, sanitized output of the step that produced an operation outcome. */
data class DockerStackOperationDiagnostics(val lines: List<String>, val truncated: Boolean)

/** Kept here with the models so routes cannot be scattered through Compose screens. */
object DockerRoutes {
    private const val ROOT = "/api/v1.0/docker"
    const val STATUS = "$ROOT/status"
    const val CONTAINERS = "$ROOT/containers"
    const val IMAGES = "$ROOT/images"
    const val NETWORKS = "$ROOT/networks"
    const val VOLUMES = "$ROOT/volumes"
    const val STACKS = "$ROOT/stacks"
    const val STACK_DEPLOY = "$ROOT/stacks/deploy"
    const val STACK_PREVIEW = "$ROOT/stacks/preview"
    fun containerAction(id: String, action: String) = "$CONTAINERS/${encodePath(id)}/$action"
    fun containerLogs(id: String, tail: Int) = "$CONTAINERS/${encodePath(id)}/logs?tail=$tail"
    fun stackServices(name: String) = "$STACKS/${encodePath(name)}/services"
    fun volumeDetails(name: String) = "$VOLUMES/${encodePath(name)}"
    fun volumeDelete(name: String, confirmed: Boolean) = "$VOLUMES/${encodePath(name)}?confirmed=$confirmed"
    fun stackAction(name: String, action: String) = "$STACKS/${encodePath(name)}/$action"
    fun stackOperations(name: String, limit: Int) = "$STACKS/${encodePath(name)}/operations?limit=$limit"
    fun stackOperation(operationId: String) = "$ROOT/stack-operations/${encodePath(operationId)}"
    fun stackOperationDiagnostics(operationId: String) = "$ROOT/stack-operations/${encodePath(operationId)}/diagnostics"
    fun stackOperationCancel(operationId: String) = "$ROOT/stack-operations/${encodePath(operationId)}/cancel"
    private fun encodePath(value: String) = java.net.URLEncoder.encode(value, "UTF-8")
}

internal object DockerWire {
    fun status(body: String): DockerStatus = JSONObject(body).let { json ->
        DockerStatus(json.optBoolean("isAvailable"), json.optString("problemCode"), json.optString("serverVersion"),
            json.optString("operatingSystem"), json.optString("architecture"))
    }
    fun containers(body: String) = array(body) { json -> DockerContainer(json.optString("id"), json.optString("names"), json.optString("image"), json.optString("state"), json.optString("status")) }
    fun images(body: String) = array(body) { json -> DockerImage(json.optString("id"), json.optString("repository"), json.optString("tag"), json.optString("size"), json.optString("createdSince")) }
    fun networks(body: String) = array(body) { json -> DockerNetwork(json.optString("id"), json.optString("name"), json.optString("driver"), json.optString("scope")) }
    fun volumes(body: String) = array(body) { json -> DockerVolume(json.optString("name"), json.optString("driver"), json.optString("mountpoint")) }
    fun volumeDetails(body: String) = JSONObject(body).let { json ->
        DockerVolumeDetails(json.optString("name"), json.optString("driver"), json.optString("mountpoint"),
            json.optJSONArray("usedBy").strings())
    }
    fun stacks(body: String) = array(body) { json -> DockerStack(json.optString("name"), json.optString("status"), json.optString("configFiles"), json.optString("configDirectory")) }
    fun services(body: String) = array(body) { json -> service(json) }
    fun logs(body: String) = JSONObject(body).let { DockerLogs(it.optJSONArray("lines").strings(), it.optBoolean("truncated")) }
    fun operation(body: String) = JSONObject(body).let { DockerOperation(it.optBoolean("success"), it.optString("problemCode"), it.optJSONArray("messages").strings()) }
    fun preview(body: String): DockerStackPreview = JSONObject(body).let { json ->
        DockerStackPreview(
            json.optString("projectName"), json.optString("definitionVersion"),
            (json.optJSONArray("services") ?: JSONArray()).let { array ->
                List(array.length()) { index ->
                    val item = array.getJSONObject(index)
                    DockerStackPreviewService(item.optString("service"), item.optString("image"), item.optJSONArray("ports").strings())
                }
            },
            json.optJSONArray("volumes").strings(), json.optJSONArray("networks").strings())
    }
    fun stackOperation(body: String): DockerStackOperation = JSONObject(body).let { json ->
        DockerStackOperation(
            json.optString("operationId"), json.optString("projectName"),
            DockerStackOperationKind.fromWire(json.optString("kind")), DockerStackOperationState.fromWire(json.optString("state")),
            json.optString("stage"), json.optNullableString("problemCode"), json.optNullableString("recoveryProblemCode"),
            json.optString("createdAt"), json.optBoolean("cancellable"),
            (json.optJSONArray("services") ?: JSONArray()).let { array -> List(array.length()) { service(array.getJSONObject(it)) } })
    }
    fun stackOperations(body: String) = array(body) { json -> stackOperation(json.toString()) }
    fun diagnostics(body: String) = JSONObject(body).let { DockerStackOperationDiagnostics(it.optJSONArray("lines").strings(), it.optBoolean("truncated")) }
    private fun service(json: JSONObject) = DockerStackService(json.optString("service"), json.optString("container"), json.optString("image"), json.optString("state"), json.optString("status"))
    private fun <T> array(body: String, mapper: (JSONObject) -> T): List<T> = JSONArray(body).let { array -> List(array.length()) { mapper(array.getJSONObject(it)) } }
    private fun JSONArray?.strings(): List<String> = if (this == null) emptyList() else List(length()) { optString(it) }
}
