package app.relaxkonos.mobile.core.net

import java.net.URI
import org.json.JSONArray
import org.json.JSONObject

enum class DockerEngineAction(val wire: String) { Start("start"), Stop("stop"), Restart("restart") }
data class DockerEngineResult(val success: Boolean, val problemCode: String, val status: DockerStatus?)
data class DockerImageMirror(val id: String, val name: String, val endpoint: String, val selected: Boolean) {
    val default get() = id == DEFAULT_ID
    companion object { const val DEFAULT_ID = "00000000-0000-0000-0000-000000000000" }
}
data class DockerMirrorRequest(val name: String, val endpoint: String) {
    fun body() = JsonBody().string("name", name).string("endpoint", endpoint)
}
data class DockerControlFacts(val status: DockerStatus, val mirrors: List<DockerImageMirror>)
enum class DockerControlKind { EngineStart, EngineStop, EngineRestart, MirrorCreate, MirrorUpdate, MirrorDelete, MirrorSelect }
data class DockerControlChange(val kind: DockerControlKind, val target: String? = null, val mirror: DockerMirrorRequest? = null) {
    val engine get() = when (kind) {
        DockerControlKind.EngineStart -> DockerEngineAction.Start
        DockerControlKind.EngineStop -> DockerEngineAction.Stop
        DockerControlKind.EngineRestart -> DockerEngineAction.Restart
        else -> null
    }
}
object DockerControlRoutes {
    const val MIRRORS = "/api/v1.0/image-mirrors/docker"
    fun mirror(id: String) = "$MIRRORS/${InstallationRoutes.canonicalId(id)}"
    fun engine(action: DockerEngineAction) = "/api/v1.0/docker/engine/${action.wire}"
}
object DockerMirrorValidation {
    fun valid(value: DockerMirrorRequest): Boolean {
        if (value.name.trim().length !in 1..80 || value.name.any(Char::isISOControl)) return false
        val candidate = value.endpoint.trim().let { if ("://" in it) it else "https://$it" }
        if (candidate.length > 255 || candidate.any(Char::isISOControl)) return false
        return runCatching { URI(candidate).let {
            it.scheme.equals("https", true) && !it.host.isNullOrBlank() && it.userInfo == null && it.query == null && it.fragment == null &&
                it.path in listOf("", "/") && (it.port == -1 || it.port in 1..65535)
        } }.getOrDefault(false)
    }
}
internal object DockerControlWire {
    fun engine(body: String) = JSONObject(body).let { json ->
        require(json.has("status"))
        DockerEngineResult(json.getBoolean("success"), json.getString("problemCode"),
            if (json.isNull("status")) null else DockerWire.status(json.getJSONObject("status").toString()))
    }
    fun mirror(body: String) = parseMirror(JSONObject(body))
    fun mirrors(body: String): List<DockerImageMirror> = JSONArray(body).let { array ->
        require(array.length() in 1..1000)
        List(array.length()) { parseMirror(array.getJSONObject(it)) }
    }.also { mirrors ->
        require(mirrors.map { it.id }.distinct().size == mirrors.size && mirrors.count { it.default } == 1 && mirrors.count { it.selected } == 1)
    }
    private fun parseMirror(json: JSONObject): DockerImageMirror {
        require(json.getString("target") == "docker")
        return DockerImageMirror(InstallationRoutes.canonicalId(json.getString("id")), json.getString("name"),
            json.getString("endpoint"), json.getBoolean("isSelected"))
    }
}
