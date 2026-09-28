package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import org.json.JSONArray
import org.json.JSONObject

data class GitRepository(val id: String, val name: String, val path: String, val currentBranch: String?)
data class GitBranch(val name: String, val current: Boolean, val remote: Boolean)
data class GitChange(val path: String, val status: String)
data class GitStatus(
    val branch: String,
    val staged: List<GitChange>,
    val unstaged: List<GitChange>,
    val untracked: List<GitChange>,
    val conflicts: List<GitChange>,
    val ahead: Int,
    val behind: Int,
)
data class GitTextFile(val path: String, val content: String, val version: String)
data class GitOperation(val success: Boolean, val operation: String, val requiresCredentials: Boolean)

object GitRoutes {
    private const val ROOT = "/api/v1.0/git/repositories"
    const val REPOSITORIES = ROOT
    fun repository(id: String): String = "$ROOT/${segment(id)}"
    fun status(id: String): String = "${repository(id)}/status"
    fun branches(id: String): String = "${repository(id)}/branches"
    fun textFile(id: String, path: String): String = "${repository(id)}/text-file?path=${query(path)}"
    fun commit(id: String): String = "${repository(id)}/commit"
    fun push(id: String): String = "${repository(id)}/push"
    private fun segment(value: String): String {
        require(value.isNotBlank())
        return URLEncoder.encode(value, "UTF-8")
    }
    private fun query(value: String): String = URLEncoder.encode(value, "UTF-8")
}

internal object GitWire {
    fun repository(payload: String): GitRepository = JSONObject(payload).let { json ->
        GitRepository(json.getString("id"), json.getString("name"), json.getString("path"), json.optStringOrNull("currentBranch"))
    }
    fun repositories(payload: String): List<GitRepository> = JSONArray(payload).objects { json ->
        repository(json.toString())
    }
    fun branches(payload: String): List<GitBranch> = JSONArray(payload).objects { json ->
        GitBranch(json.getString("name"), json.getBoolean("isCurrent"), json.getBoolean("isRemote"))
    }
    fun status(payload: String): GitStatus = JSONObject(payload).let { json ->
        GitStatus(json.getString("branch"), json.getJSONArray("staged").changes(),
            json.getJSONArray("unstaged").changes(), json.getJSONArray("untracked").changes(),
            json.getJSONArray("conflicts").changes(), json.getInt("ahead"), json.getInt("behind"))
    }
    fun textFile(payload: String): GitTextFile = JSONObject(payload).let { json ->
        GitTextFile(json.getString("path"), json.getString("content"), json.getString("version"))
    }
    fun operation(payload: String): GitOperation = JSONObject(payload).let { json ->
        GitOperation(json.getBoolean("success"), json.getString("operation"), json.getBoolean("requiresCredentials"))
    }
    private fun JSONArray.changes(): List<GitChange> = objects { GitChange(it.getString("path"), it.getString("status")) }
    private fun JSONObject.optStringOrNull(key: String): String? =
        if (!has(key) || isNull(key)) null else getString(key)
    private fun <T> JSONArray.objects(read: (JSONObject) -> T): List<T> = (0 until length()).map { read(getJSONObject(it)) }
}
