package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

data class GitBuildCredential(val id: String, val name: String)
data class GitBuildResolved(val repositoryUrl: String, val reference: String, val commitSha: String)
data class GitBuildRef(val name: String, val commitSha: String)
data class GitBuildRequest(
    val repositoryUrl: String, val reference: String, val commitSha: String,
    val contextDirectory: String, val dockerfile: String, val credentialId: String?,
)
data class GitBuildOperation(
    val id: String, val repositoryUrl: String, val reference: String, val commitSha: String,
    val contextDirectory: String, val dockerfile: String, val state: String, val problemCode: String?,
    val imageReference: String?, val imageId: String?, val logs: List<String>,
)

object GitBuildRoutes {
    const val ROOT = "/api/v1.0/git/builds"
    const val CREDENTIALS = "$ROOT/credentials"
    const val RESOLVE = "$ROOT/resolve"
    const val REFS = "$ROOT/refs"
    fun operation(id: String) = "$ROOT/$id"
    fun cancel(id: String) = "$ROOT/$id/cancel"
}

internal object GitBuildWire {
    fun credential(payload: String) = JSONObject(payload).let { GitBuildCredential(it.getString("id"), it.getString("name")) }
    fun credentials(payload: String) = JSONArray(payload).let { array -> (0 until array.length()).map { credential(array.getJSONObject(it).toString()) } }
    fun resolved(payload: String) = JSONObject(payload).let {
        GitBuildResolved(it.getString("repositoryUrl"), it.getString("reference"), it.getString("commitSha"))
    }
    fun refs(payload: String) = JSONArray(payload).let { array ->
        (0 until array.length()).map { array.getJSONObject(it).let { ref -> GitBuildRef(ref.getString("name"), ref.getString("commitSha")) } }
    }
    fun operation(payload: String) = JSONObject(payload).let { json ->
        GitBuildOperation(json.getString("id"), json.getString("repositoryUrl"), json.getString("reference"),
            json.getString("commitSha"), json.getString("contextDirectory"), json.getString("dockerfile"),
            json.getString("state"), json.optStringOrNull("problemCode"), json.optStringOrNull("imageReference"),
            json.optStringOrNull("imageId"), json.getJSONArray("logs").let { lines ->
                (0 until lines.length()).map { lines.getString(it) }
            })
    }
    fun operations(payload: String) = JSONArray(payload).let { array ->
        (0 until array.length()).map { operation(array.getJSONObject(it).toString()) }
    }
    private fun JSONObject.optStringOrNull(key: String): String? =
        if (isNull(key) || !has(key)) null else getString(key)
}
