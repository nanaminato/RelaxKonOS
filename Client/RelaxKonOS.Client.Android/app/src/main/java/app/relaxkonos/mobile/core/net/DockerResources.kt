package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

data class DockerContainerDetails(val id: String, val name: String, val image: String, val created: String, val state: String,
    val status: String, val command: String, val workingDirectory: String, val restartPolicy: String,
    val ports: List<String>, val mounts: List<String>, val networks: List<String>, val environment: List<String>, val labels: Map<String, String>)
data class DockerContainerStats(val containerId: String, val cpuPercent: String, val memoryUsage: String, val networkIo: String, val blockIo: String)
data class DockerNetworkDetails(val id: String, val name: String, val driver: String, val scope: String, val containers: List<String>, val labels: Map<String, String>)
data class DockerContainerResources(val cpuCores: Double? = null, val memoryBytes: Long? = null, val pidsLimit: Int? = null,
    val logDriver: String? = null, val logOptions: List<String> = emptyList()) {
    fun body() = JsonBody().apply {
        raw("cpuCores", cpuCores?.toString() ?: "null"); raw("memoryBytes", memoryBytes?.toString() ?: "null")
        raw("pidsLimit", pidsLimit?.toString() ?: "null"); raw("logDriver", logDriver?.let { JSONObject.quote(it) } ?: "null")
        raw("logOptions", JSONArray(logOptions).toString())
    }
}
data class DockerContainerCreate(val name: String, val image: String, val arguments: List<String>, val ports: List<String> = emptyList(),
    val environment: List<String> = emptyList(), val mounts: List<String> = emptyList(), val network: String? = null,
    val restartPolicy: String? = null, val labels: List<String> = emptyList(), val resources: DockerContainerResources? = null) {
    fun body() = JsonBody().string("name", name).string("image", image).apply {
        raw("arguments", JSONArray(arguments).toString()); raw("ports", JSONArray(ports).toString()); raw("environment", JSONArray(environment).toString())
        raw("mounts", JSONArray(mounts).toString()); raw("labels", JSONArray(labels).toString())
        raw("network", network?.let { JSONObject.quote(it) } ?: "null"); raw("restartPolicy", restartPolicy?.let { JSONObject.quote(it) } ?: "null")
        if (resources == null) raw("resources", "null") else objectField("resources", resources.body())
    }
}
enum class DockerResourceKind { Containers, Images, Networks, Volumes }
enum class DockerResourceAction { CreateContainer, RenameContainer, Start, Stop, Restart, Pause, Unpause, DeleteContainer, PullImage, DeleteImage, CreateNetwork, DeleteNetwork, CreateVolume, DeleteVolume }
data class DockerResourceChange(val action: DockerResourceAction, val target: String? = null, val value: String? = null,
    val driver: String? = null, val container: DockerContainerCreate? = null, val labels: List<String> = emptyList(), val force: Boolean = false)
data class DockerResourceFacts(val status: DockerStatus, val containers: List<DockerContainer>?, val images: List<DockerImage>?,
    val networks: List<DockerNetwork>?, val volumes: List<DockerVolume>?)
data class DockerResourceTarget(val kind: DockerResourceKind, val id: String, val container: DockerContainerDetails? = null,
    val network: DockerNetworkDetails? = null, val volume: DockerVolumeDetails? = null) {
    val labels get() = container?.labels ?: network?.labels ?: volume?.labels.orEmpty()
    val applicationId get() = labels["relaxkonos.application-id"]?.takeIf { labels["relaxkonos.owner"] == "application-deployment" }
    val stack get() = labels["com.docker.compose.project"]?.takeIf(String::isNotBlank)
    val managed get() = labels.keys.any { it.startsWith("relaxkonos.") || it.startsWith("com.docker.compose.") }
}
object DockerResourceRoutes {
    private const val ROOT = "/api/v1.0/docker"
    fun segment(value: String): String { require(value.isNotBlank() && value.length <= 256 && value.none(Char::isISOControl)); return java.net.URLEncoder.encode(value, "UTF-8").replace("+", "%20") }
    fun resource(kind: DockerResourceKind, id: String) = "$ROOT/${kind.name.lowercase()}/${segment(id)}"
    fun stats(id: String) = resource(DockerResourceKind.Containers, id) + "/stats"
    fun route(change: DockerResourceChange): String = when (change.action) {
        DockerResourceAction.CreateContainer -> DockerRoutes.CONTAINERS
        DockerResourceAction.RenameContainer -> resource(DockerResourceKind.Containers, requireNotNull(change.target))
        DockerResourceAction.Start, DockerResourceAction.Stop, DockerResourceAction.Restart, DockerResourceAction.Pause, DockerResourceAction.Unpause, DockerResourceAction.DeleteContainer ->
            resource(DockerResourceKind.Containers, requireNotNull(change.target)) + "/" + if (change.action == DockerResourceAction.DeleteContainer) "delete" else change.action.name.lowercase()
        DockerResourceAction.PullImage -> DockerRoutes.IMAGES + "/pull"
        DockerResourceAction.DeleteImage -> resource(DockerResourceKind.Images, requireNotNull(change.target))
        DockerResourceAction.CreateNetwork -> DockerRoutes.NETWORKS
        DockerResourceAction.DeleteNetwork -> resource(DockerResourceKind.Networks, requireNotNull(change.target)) + "?confirmed=true"
        DockerResourceAction.CreateVolume -> DockerRoutes.VOLUMES
        DockerResourceAction.DeleteVolume -> resource(DockerResourceKind.Volumes, requireNotNull(change.target)) + "?confirmed=true"
    }
    fun method(action: DockerResourceAction) = when (action) {
        DockerResourceAction.RenameContainer -> "PUT"
        DockerResourceAction.DeleteImage, DockerResourceAction.DeleteNetwork, DockerResourceAction.DeleteVolume -> "DELETE"
        else -> "POST"
    }
    fun body(change: DockerResourceChange): JsonBody? = when (change.action) {
        DockerResourceAction.CreateContainer -> requireNotNull(change.container).body()
        DockerResourceAction.RenameContainer -> JsonBody().string("name", requireNotNull(change.value))
        DockerResourceAction.PullImage -> JsonBody().string("imageReference", requireNotNull(change.value)).bool("confirmed", true)
        DockerResourceAction.DeleteImage -> JsonBody().string("imageReference", requireNotNull(change.target)).bool("confirmed", true)
        DockerResourceAction.CreateNetwork, DockerResourceAction.CreateVolume -> JsonBody().string("name", requireNotNull(change.value))
            .string("driver", requireNotNull(change.driver)).bool("confirmed", true).apply {
                if (change.action == DockerResourceAction.CreateVolume) raw("labels", JSONArray(change.labels).toString())
            }
        DockerResourceAction.DeleteNetwork, DockerResourceAction.DeleteVolume -> null
        else -> JsonBody().bool("force", change.force).bool("confirmed", true)
    }
}
object DockerResourceValidation {
    fun name(value: String) = value.length in 3..128 && !value.startsWith('-') && value.all { it.isLetterOrDigit() && it.code < 128 || it in "_.-" }
    fun image(value: String) = value.length in 1..255 && !value.startsWith('-') && value.all { it.isLetterOrDigit() && it.code < 128 || it in "/:._-" }
    fun option(value: String) = value.length in 1..4096 && value.none(Char::isISOControl)
    fun labels(values: List<String>) = values.size <= 32 && values.all {
        it.length in 3..256 && '=' in it && !it.substringBefore('=').let { key -> key.startsWith("relaxkonos.") || key.startsWith("com.docker.compose.") } &&
            it.all { c -> c.isLetterOrDigit() && c.code < 128 || c in "_-.=/: " || c.code in 128..65535 }
    }
    fun create(value: DockerContainerCreate): Boolean {
        val r = value.resources
        return name(value.name) && image(value.image) && value.arguments.size <= 64 && value.arguments.all(::option) &&
            value.ports.size <= 32 && value.ports.all(::option) && value.environment.size <= 64 && value.environment.all(::option) &&
            value.mounts.size <= 32 && value.mounts.all(::option) && labels(value.labels) &&
            (value.network.isNullOrEmpty() || name(value.network)) && (value.restartPolicy.isNullOrEmpty() || value.restartPolicy in setOf("no", "always", "unless-stopped", "on-failure")) &&
            (r?.cpuCores == null || r.cpuCores.isFinite() && r.cpuCores > 0 && r.cpuCores <= 1024) &&
            (r?.memoryBytes == null || r.memoryBytes in 1..(1L shl 42)) && (r?.pidsLimit == null || r.pidsLimit in 1..1048576) &&
            (r?.logDriver.isNullOrEmpty() || r?.logDriver in setOf("json-file", "local", "journald", "syslog", "none")) &&
            (r == null || r.logOptions.size <= 8 && r.logOptions.all { it.length in 3..128 && '=' in it && it.all { c -> c.isLetterOrDigit() && c.code < 128 || c in "_-.=/" } })
    }
    fun identity(expected: String, actual: String) = expected == actual || expected.length >= 3 && expected.all { it in "0123456789abcdefABCDEF" } && actual.all { it in "0123456789abcdefABCDEF" } && actual.startsWith(expected)
}
internal object DockerResourceWire {
    fun container(body: String) = JSONObject(body).let { j -> DockerContainerDetails(j.getString("id"), j.getString("name"), j.getString("image"), j.getString("created"),
        j.getString("state"), j.getString("status"), j.getString("command"), j.getString("workingDirectory"), j.getString("restartPolicy"),
        strings(j, "ports"), strings(j, "mounts"), strings(j, "networks"), strings(j, "environment"), labels(j)) }
    fun network(body: String) = JSONObject(body).let { DockerNetworkDetails(it.getString("id"), it.getString("name"), it.getString("driver"), it.getString("scope"), strings(it, "containers"), labels(it)) }
    fun stats(body: String) = JSONObject(body).let { DockerContainerStats(it.getString("containerId"), it.getString("cpuPercent"), it.getString("memoryUsage"), it.getString("networkIo"), it.getString("blockIo")) }
    fun volume(body: String) = JSONObject(body).let { DockerVolumeDetails(it.getString("name"), it.getString("driver"), it.getString("mountpoint"), strings(it, "usedBy"), labels(it)) }
    private fun strings(j: JSONObject, key: String): List<String> = j.getJSONArray(key).let { a -> require(a.length() <= 10000); List(a.length()) { a.getString(it) } }
    private fun labels(j: JSONObject): Map<String, String> = j.getJSONObject("labels").let { json ->
        require(json.length() <= 10000); json.keys().asSequence().associateWith { json.getString(it) }
    }
}
