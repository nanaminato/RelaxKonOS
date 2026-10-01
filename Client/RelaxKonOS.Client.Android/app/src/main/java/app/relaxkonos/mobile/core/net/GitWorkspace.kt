package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject
import java.net.URLEncoder

data class GitEngine(val available: Boolean, val problemCode: String, val version: String?, val canInstall: Boolean)
data class GitDiff(val path: String, val version: String, val patch: String, val additions: Int, val deletions: Int, val binary: Boolean, val truncated: Boolean)
data class GitCommit(val sha: String, val shortSha: String, val author: String, val date: String, val subject: String)
data class GitCommitDetail(val sha: String, val author: String, val date: String, val subject: String, val body: String?, val parents: List<String>, val changedFiles: List<GitChange>)
data class GitConflictState(val operation: String?, val paths: List<String>)
data class GitConflictFile(val path: String, val revision: String, val base: String?, val ours: String?, val theirs: String?, val result: String?, val canEdit: Boolean)
enum class GitAction(val operation: String) {
    Checkout("checkout"), CreateBranch("create-branch"), DeleteBranch("delete-branch"), Fetch("fetch"), Pull("pull"),
    Stage("stage"), Unstage("unstage"), Commit("commit"), Push("push"), Resolve("resolve"), Continue("continue"), Abort("abort")
}
data class GitMutation(val action: GitAction, val branch: String? = null, val strategy: String = "merge",
    val paths: List<String> = emptyList(), val message: String? = null, val conflict: GitConflictFile? = null,
    val choice: String? = null, val content: String? = null, val operation: String? = null)

object GitWorkspacePolicy {
    fun path(value: String): Boolean = value.isNotBlank() && !value.startsWith('/') && !value.contains('\\') && !value.contains(':') &&
        value.none { it == '\u0000' } && value.split('/').none { it in listOf("", ".", "..") || it.equals(".git", true) }
    fun branch(value: String): Boolean = value.isNotBlank() && !value.startsWith('-') && !value.endsWith('.') && !value.contains("..") &&
        !value.contains("@{") && value != "@" && value.none { it <= ' ' || it in "~^:?*[\\" || it == '\u007f' } &&
        value.split('/').none { it.isEmpty() || it.startsWith('.') || it.endsWith(".lock") }
    fun validate(change: GitMutation) {
        require(change.paths.size <= 5000 && change.paths.distinct().size == change.paths.size && change.paths.all(::path))
        when (change.action) {
            GitAction.Checkout, GitAction.CreateBranch, GitAction.DeleteBranch -> require(change.branch?.let(::branch) == true)
            GitAction.Stage, GitAction.Unstage -> require(change.paths.isNotEmpty())
            GitAction.Commit -> require(change.paths.isEmpty() && !change.message.isNullOrBlank() && change.message.length <= 16384)
            GitAction.Pull -> require(change.strategy in listOf("merge", "rebase", "ff-only"))
            GitAction.Resolve -> {
                val file = requireNotNull(change.conflict); require(path(file.path))
                require(change.choice in listOf("ours", "theirs", "delete", "edited"))
                if (file.canEdit && change.choice == "ours") require(file.ours != null)
                if (file.canEdit && change.choice == "theirs") require(file.theirs != null)
                if (change.choice == "edited") require(file.canEdit && change.content != null &&
                    TextEditorPolicy.valid(change.content, "utf-8", false) && change.content.toByteArray(Charsets.UTF_8).size <= 200 * 1024 &&
                    change.content.lines().none { line -> listOf("<<<<<<<", "=======", ">>>>>>>", "|||||||").any(line::startsWith) })
            }
            GitAction.Continue, GitAction.Abort -> require(change.operation in listOf("merge", "rebase", "revert", "cherry-pick"))
            GitAction.Fetch, GitAction.Push -> Unit
        }
    }
}
object GitWorkspaceRoutes {
    const val ENGINE = "/api/v1.0/git/engine/status"
    fun diff(id: String, path: String, staged: Boolean, reference: String? = null) = "${GitRoutes.repository(id)}/diff?path=${q(path)}&staged=$staged" +
        (reference?.let { "&ref=${q(it)}" } ?: "")
    fun log(id: String, skip: Int, search: String) = "${GitRoutes.repository(id)}/log?limit=50&skip=$skip&search=${q(search)}"
    fun commit(id: String, sha: String) = "${GitRoutes.repository(id)}/commits/${q(sha)}"
    fun conflicts(id: String) = "${GitRoutes.repository(id)}/conflicts"
    fun conflict(id: String, path: String) = "${conflicts(id)}/file?path=${q(path)}"
    fun mutation(id: String, change: GitMutation): String = GitRoutes.repository(id) + when (change.action) {
        GitAction.Checkout -> "/checkout"
        GitAction.CreateBranch -> "/branches"
        GitAction.DeleteBranch -> "/branches/${q(requireNotNull(change.branch))}"
        GitAction.Fetch -> "/fetch"
        GitAction.Pull -> "/pull"
        GitAction.Stage -> "/stage"
        GitAction.Unstage -> "/unstage"
        GitAction.Commit -> "/commit"
        GitAction.Push -> "/push"
        GitAction.Resolve -> "/resolve"
        GitAction.Continue, GitAction.Abort -> "/conflicts/operation"
    }
    private fun q(value: String) = URLEncoder.encode(value, "UTF-8")
}
internal object GitWorkspaceWire {
    fun engine(payload: String) = JSONObject(payload).let { GitEngine(it.getBoolean("isAvailable"), it.getString("problemCode"), it.nullable("version"), it.getBoolean("canAutoInstall")) }
    fun diff(payload: String) = JSONObject(payload).let { GitDiff(it.getString("path"), it.getString("version"), it.getString("patch"), it.getInt("additions"), it.getInt("deletions"), it.getBoolean("binary"), it.getBoolean("truncated")).also {
        require(GitWorkspacePolicy.path(it.path) && it.version.matches(Regex("[0-9a-f]{64}")) && it.additions >= 0 && it.deletions >= 0)
    } }
    fun log(payload: String): List<GitCommit> = JSONArray(payload).objects { GitCommit(it.getString("sha"), it.getString("shortSha"), it.getString("author"), it.getString("authorDate"), it.getString("subject")) }
    fun detail(payload: String) = JSONObject(payload).let { GitCommitDetail(it.getString("sha"), it.getString("author"), it.getString("date"), it.getString("subject"), it.nullable("body"), it.getJSONArray("parents").strings(), it.getJSONArray("changedFiles").objects { row -> GitChange(row.getString("path"), row.getString("status"), row.nullable("oldPath")) }).also {
        require((it.parents + it.sha).all { sha -> sha.matches(Regex("[0-9a-f]{40}|[0-9a-f]{64}")) })
        require(it.changedFiles.all { row -> GitWorkspacePolicy.path(row.path) && (row.oldPath == null || GitWorkspacePolicy.path(row.oldPath)) })
    } }
    fun conflicts(payload: String) = JSONObject(payload).let {
        val operation = it.nullable("operation"); require(operation == null || operation in listOf("merge", "rebase", "revert", "cherry-pick"))
        GitConflictState(operation, it.getJSONArray("paths").strings().also { paths -> require(paths.all(GitWorkspacePolicy::path)) })
    }
    fun conflict(payload: String) = JSONObject(payload).let { GitConflictFile(it.getString("path"), it.getString("revision"),
        it.nullable("baseVersion"), it.nullable("oursVersion"), it.nullable("theirsVersion"), it.nullable("result"), it.getBoolean("canEdit")).also {
        require(GitWorkspacePolicy.path(it.path) && it.revision.matches(Regex("[0-9a-fA-F]{64}")))
    } }
    private fun JSONObject.nullable(key: String): String? { require(has(key)); return if (isNull(key)) null else getString(key) }
    private fun JSONArray.strings(): List<String> { require(length() <= 5000); return (0 until length()).map { getString(it) } }
    private fun <T> JSONArray.objects(read: (JSONObject) -> T): List<T> { require(length() <= 5000); return (0 until length()).map { read(getJSONObject(it)) } }
}
