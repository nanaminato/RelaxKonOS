package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

/** Android projection of the Docker REST contract. YAML execution remains entirely server-owned. */
data class DockerStatus(val available: Boolean, val problemCode: String, val serverVersion: String, val operatingSystem: String, val architecture: String)
data class DockerContainer(val id: String, val names: String, val image: String, val state: String, val status: String)
data class DockerImage(val id: String, val repository: String, val tag: String, val size: String, val createdSince: String)
data class DockerNetwork(val id: String, val name: String, val driver: String, val scope: String)
data class DockerVolume(val name: String, val driver: String, val mountpoint: String)
data class DockerStack(val name: String, val status: String, val configFiles: String, val configDirectory: String)
data class DockerStackService(val service: String, val container: String, val image: String, val state: String, val status: String)
data class DockerLogs(val lines: List<String>, val truncated: Boolean)
data class DockerOperation(val success: Boolean, val problemCode: String, val messages: List<String>)

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
    const val STACK_VALIDATE = "$ROOT/stacks/validate"
    fun containerAction(id: String, action: String) = "$CONTAINERS/${encodePath(id)}/$action"
    fun containerLogs(id: String, tail: Int) = "$CONTAINERS/${encodePath(id)}/logs?tail=$tail"
    fun stackServices(name: String) = "$STACKS/${encodePath(name)}/services"
    fun stackAction(name: String, action: String) = "$STACKS/${encodePath(name)}/$action"
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
    fun stacks(body: String) = array(body) { json -> DockerStack(json.optString("name"), json.optString("status"), json.optString("configFiles"), json.optString("configDirectory")) }
    fun services(body: String) = array(body) { json -> DockerStackService(json.optString("service"), json.optString("container"), json.optString("image"), json.optString("state"), json.optString("status")) }
    fun logs(body: String) = JSONObject(body).let { DockerLogs(it.optJSONArray("lines").strings(), it.optBoolean("truncated")) }
    fun operation(body: String) = JSONObject(body).let { DockerOperation(it.optBoolean("success"), it.optString("problemCode"), it.optJSONArray("messages").strings()) }
    private fun <T> array(body: String, mapper: (JSONObject) -> T): List<T> = JSONArray(body).let { array -> List(array.length()) { mapper(array.getJSONObject(it)) } }
    private fun JSONArray?.strings(): List<String> = if (this == null) emptyList() else List(length()) { optString(it) }
}
