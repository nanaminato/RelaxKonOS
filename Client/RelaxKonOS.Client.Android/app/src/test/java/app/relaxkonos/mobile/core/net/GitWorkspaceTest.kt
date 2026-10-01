package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class GitWorkspaceTest {
    @Test fun `status requires an opaque current configuration version`() {
        val payload = """{"branch":"main","staged":[],"unstaged":[],"untracked":[],"conflicts":[],"ahead":0,"behind":0,"upstream":null,"isDetached":false,"configVersion":"${"a".repeat(64)}"}"""
        assertEquals("a".repeat(64), GitWire.status(payload).configVersion)
        assertTrue(runCatching { GitWire.status(JSONObject(payload).apply { remove("configVersion") }.toString()) }.isFailure)
        assertTrue(runCatching { GitWire.status(JSONObject(payload).put("configVersion", "raw config").toString()) }.isFailure)
    }
    @Test fun `current diff and branch contracts require versions without compatibility defaults`() {
        val diff = """{"path":"a.txt","version":"${"a".repeat(64)}","patch":"p","additions":1,"deletions":0,"binary":false,"truncated":false}"""
        assertEquals("a".repeat(64), GitWorkspaceWire.diff(diff).version)
        assertTrue(runCatching { GitWorkspaceWire.diff(JSONObject(diff).apply { remove("version") }.toString()) }.isFailure)
        val branches = """[{"name":"main","sha":"${"b".repeat(40)}","isCurrent":true,"isRemote":false,"tracking":null,"ahead":0,"behind":0}]"""
        assertNull(GitWire.branches(branches).single().tracking)
        assertTrue(runCatching { GitWire.branches(branches.replace("\"sha\":\"${"b".repeat(40)}\",", "")) }.isFailure)
        assertTrue(runCatching { GitWorkspaceWire.conflicts("""{"paths":[]}""") }.isFailure)
        assertTrue(runCatching { GitWire.operation("""{"success":true,"operation":"push","requiresCredentials":false}""") }.isFailure)
    }
    @Test fun `literal names and branch validation reject traversal and closed conflict validation rejects markers`() {
        assertTrue(GitWorkspacePolicy.path("file * 文件.txt"))
        listOf("/a", "../a", "a/../b", ".git/config", "a\\b", "C:a", "a//b").forEach { assertFalse(it, GitWorkspacePolicy.path(it)) }
        listOf("-x", "a..b", "a@{b", "refs/heads/", ".hidden", "x.lock", "a b", "a*").forEach { assertFalse(it, GitWorkspacePolicy.branch(it)) }
        assertTrue(GitWorkspacePolicy.branch("feature/文件"))
        val file = GitConflictFile("a", "a".repeat(64), null, "", "theirs", "", true)
        assertTrue(runCatching { GitWorkspacePolicy.validate(GitMutation(GitAction.Resolve, conflict = file, choice = "edited", content = "<<<<<<< HEAD\nx\n=======\ny\n>>>>>>> end")) }.isFailure)
        assertTrue(runCatching { GitWorkspacePolicy.validate(GitMutation(GitAction.Resolve, conflict = file, choice = "edited", content = "界".repeat(70000))) }.isFailure)
        GitWorkspacePolicy.validate(GitMutation(GitAction.Resolve, conflict = file, choice = "edited", content = "resolved\r\n"))
    }
    @Test fun `all mutation routes send explicit current intent without credentials or fake task keys`() = runTest {
        val requests = mutableListOf<List<String>>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { e ->
            requests += listOf(e.requestMethod, e.requestURI.toString(), e.requestBody.readBytes().decodeToString(),
                e.requestHeaders.getFirst("Authorization"), e.requestHeaders.getFirst("Idempotency-Key").orEmpty())
            val response = """{"success":true,"operation":"push","requiresCredentials":false,"conflicts":null}""".toByteArray()
            e.sendResponseHeaders(200, response.size.toLong()); e.responseBody.use { it.write(response) }; e.close()
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            val conflict = GitConflictFile("a 文件.txt", "a".repeat(64), "", "", "", "", true)
            val changes = listOf(GitMutation(GitAction.Checkout, branch = "feature/文件"), GitMutation(GitAction.CreateBranch, branch = "new"),
                GitMutation(GitAction.DeleteBranch, branch = "feature/文件"), GitMutation(GitAction.Fetch), GitMutation(GitAction.Pull, strategy = "rebase"),
                GitMutation(GitAction.Stage, paths = listOf("a 文件.txt")), GitMutation(GitAction.Unstage, paths = listOf("a 文件.txt")),
                GitMutation(GitAction.Commit, message = "subject\n\nbody"), GitMutation(GitAction.Push),
                GitMutation(GitAction.Resolve, conflict = conflict, choice = "edited", content = "resolved"),
                GitMutation(GitAction.Continue, operation = "merge"), GitMutation(GitAction.Abort, operation = "rebase"))
            changes.forEach { assertTrue(api.gitMutation(url, "token", "repo", it) is ApiResult.Success) }
            assertEquals(12, requests.size); assertEquals("DELETE", requests[2][0]); assertTrue(requests[2][1].contains("feature%2F%E6%96%87%E4%BB%B6"))
            assertFalse(JSONObject(requests[0][2]).getBoolean("createIfMissing"))
            assertFalse(JSONObject(requests[1][2]).getBoolean("resetExisting"))
            assertEquals("rebase", JSONObject(requests[4][2]).getString("strategy"))
            assertEquals("a 文件.txt", JSONObject(requests[5][2]).getJSONArray("paths").getString(0))
            assertEquals(0, JSONObject(requests[7][2]).getJSONArray("paths").length()); assertFalse(JSONObject(requests[7][2]).getBoolean("amend"))
            assertEquals("subject\n\nbody", JSONObject(requests[7][2]).getString("message"))
            assertFalse(JSONObject(requests[8][2]).getBoolean("saveCredentials")); assertFalse(JSONObject(requests[8][2]).has("credentials"))
            assertEquals(conflict.revision, JSONObject(requests[9][2]).getString("revision"))
            assertEquals("continue", JSONObject(requests[10][2]).getString("action")); assertEquals("abort", JSONObject(requests[11][2]).getString("action"))
            assertTrue(requests.all { it[3] == "Bearer token" && it[4].isEmpty() })
        } finally { server.stop(0) }
    }
}
