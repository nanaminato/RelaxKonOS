package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import org.json.JSONArray
import org.json.JSONObject

data class GitRepository(val id: String, val name: String, val path: String, val currentBranch: String?)
data class GitBranch(val name: String, val sha: String, val current: Boolean, val remote: Boolean, val tracking: String?, val ahead: Int, val behind: Int)
data class GitChange(val path: String, val status: String, val oldPath: String? = null)
data class GitStatus(
    val branch: String,
    val staged: List<GitChange>,
    val unstaged: List<GitChange>,
    val untracked: List<GitChange>,
    val conflicts: List<GitChange>,
    val ahead: Int,
    val behind: Int,
    val upstream: String?,
    val detached: Boolean,
    val configVersion: String,
)
data class GitOperation(val success: Boolean, val operation: String, val requiresCredentials: Boolean, val conflicts: List<String>)

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
        GitBranch(json.getString("name"), json.getString("sha"), json.getBoolean("isCurrent"), json.getBoolean("isRemote"), json.optStringOrNull("tracking"), json.getInt("ahead"), json.getInt("behind")).also {
            require(GitWorkspacePolicy.branch(it.name) && it.sha.matches(Regex("[0-9a-f]{40}|[0-9a-f]{64}")) && it.ahead >= 0 && it.behind >= 0)
        }
    }
    fun status(payload: String): GitStatus = JSONObject(payload).let { json ->
        GitStatus(json.getString("branch"), json.getJSONArray("staged").changes(),
            json.getJSONArray("unstaged").changes(), json.getJSONArray("untracked").changes(),
            json.getJSONArray("conflicts").changes(), json.getInt("ahead"), json.getInt("behind"), json.optStringOrNull("upstream"), json.getBoolean("isDetached"), json.getString("configVersion")).also {
            require(it.configVersion.matches(Regex("[0-9a-f]{64}")) && it.ahead >= 0 && it.behind >= 0 && (it.detached || GitWorkspacePolicy.branch(it.branch)))
        }
    }
    fun textFile(payload: String): RemoteTextFile = TextEditorWire.file(payload)
    fun operation(payload: String): GitOperation = JSONObject(payload).let { json ->
        require(json.has("conflicts"))
        GitOperation(json.getBoolean("success"), json.getString("operation"), json.getBoolean("requiresCredentials"),
            if (json.isNull("conflicts")) emptyList() else json.getJSONArray("conflicts").let { rows ->
                require(rows.length() <= 5000); (0 until rows.length()).map(rows::getString).also { require(it.all(GitWorkspacePolicy::path)) }
            })
    }
    private fun JSONArray.changes(): List<GitChange> = objects { GitChange(it.getString("path"), it.getString("status"), it.optStringOrNull("oldPath")).also { row ->
        require(GitWorkspacePolicy.path(row.path) && (row.oldPath == null || GitWorkspacePolicy.path(row.oldPath)))
    } }
    private fun JSONObject.optStringOrNull(key: String): String? { require(has(key)); return if (isNull(key)) null else getString(key) }
    private fun <T> JSONArray.objects(read: (JSONObject) -> T): List<T> { require(length() <= 5000); return (0 until length()).map { read(getJSONObject(it)) } }
}
