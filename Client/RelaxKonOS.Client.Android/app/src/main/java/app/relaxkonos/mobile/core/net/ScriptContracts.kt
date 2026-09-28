package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import org.json.JSONArray
import org.json.JSONObject

data class ScriptRequest(
    val executablePath: String, val arguments: List<String>, val workingDirectory: String,
    val environment: Map<String, String>, val timeoutSeconds: Int, val runAs: String,
    val approval: GuardianApproval?,
)
data class ScriptOutputLine(val timestamp: String, val stream: String, val text: String)
data class ScriptTask(
    val id: String, val executablePath: String, val runAs: String, val state: String,
    val createdAt: String, val finishedAt: String?, val exitCode: Int?, val problemCode: String?,
    val output: List<ScriptOutputLine>, val outputTruncated: Boolean,
)
data class ScriptTasksResult(val success: Boolean, val problemCode: String, val tasks: List<ScriptTask>)
data class ScriptTaskResult(val success: Boolean, val problemCode: String, val task: ScriptTask?)

object ScriptRoutes {
    private const val ROOT = "/api/v1.0/guardian/scripts"
    const val TASKS = ROOT
    fun task(id: String) = "$ROOT/${URLEncoder.encode(id, "UTF-8")}"
    fun cancel(id: String) = "${task(id)}/cancel"
}

internal object ScriptWire {
    fun tasks(body: String) = JSONObject(body).let { json ->
        val array = json.optJSONArray("scripts") ?: JSONArray()
        ScriptTasksResult(json.optBoolean("success"), json.optString("problemCode"),
            List(array.length()) { task(array.getJSONObject(it)) })
    }
    fun taskResult(body: String) = JSONObject(body).let { json ->
        ScriptTaskResult(json.optBoolean("success"), json.optString("problemCode"),
            json.optJSONObject("scriptTask")?.let(::task))
    }
    private fun task(json: JSONObject): ScriptTask {
        val lines = json.optJSONArray("output") ?: JSONArray()
        return ScriptTask(json.getString("id"), json.getString("executablePath"), json.getString("runAs"),
            json.getString("state"), json.getString("createdAt"), json.optNullable("finishedAt"),
            if (json.isNull("exitCode")) null else json.getInt("exitCode"), json.optNullable("problemCode"),
            List(lines.length()) { lines.getJSONObject(it).let { line ->
                ScriptOutputLine(line.getString("timestamp"), line.getString("stream"), line.getString("text"))
            } }, json.optBoolean("outputTruncated"))
    }
    private fun JSONObject.optNullable(name: String) = if (isNull(name)) null else getString(name)
}
