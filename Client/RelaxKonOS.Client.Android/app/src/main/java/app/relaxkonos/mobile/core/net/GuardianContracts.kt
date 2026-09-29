package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import org.json.JSONArray
import org.json.JSONObject

/** Android projection of ProcessGuardian/GuardianStatusDto.cs and related contracts. */
data class GuardianStatus(val installed: Boolean, val running: Boolean, val problemCode: String, val version: String?)
data class GuardianWorkload(
    val id: String, val name: String, val desiredState: String, val actualState: String,
    val processId: Int?, val restartCount: Int, val healthStatus: String?, val healthFailureCount: Int,
    val executablePath: String?, val workingDirectory: String?, val enabledOnBoot: Boolean, val runAs: String?,
    val lastExitCode: Int?, val lastProblemCode: String?,
)
data class GuardianHealthCheck(val type: String, val target: String?, val intervalSeconds: Int, val timeoutSeconds: Int, val failureThreshold: Int)
data class GuardianDefinition(
    val id: String, val name: String, val executablePath: String, val arguments: List<String>,
    val workingDirectory: String, val enabledOnBoot: Boolean, val stopTimeoutSeconds: Int,
    val maxRestartAttempts: Int, val healthCheck: GuardianHealthCheck?, val runAs: String?,
)
data class GuardianLog(val timestamp: String, val stream: String, val message: String)
data class GuardianOperation(val success: Boolean, val problemCode: String)
data class GuardianApproval(val username: String, val password: CharArray)

object GuardianRoutes {
    private const val ROOT = "/api/v1.0/guardian"
    const val STATUS = "$ROOT/status"
    const val WORKLOADS = "$ROOT/workloads"
    fun workload(id: String) = "$WORKLOADS/${encode(id)}"
    fun logs(id: String) = "${workload(id)}/logs"
    fun action(id: String, action: String) = "${workload(id)}/${encode(action)}"
    private fun encode(value: String) = URLEncoder.encode(value, "UTF-8")
}

internal object GuardianWire {
    fun status(body: String) = JSONObject(body).let {
        GuardianStatus(it.getBoolean("isInstalled"), it.getBoolean("isRunning"), it.optString("problemCode"), it.optNullable("version"))
    }
    fun workloads(body: String): List<GuardianWorkload> = JSONArray(body).let { array ->
        List(array.length()) { index -> workload(array.getJSONObject(index)) }
    }
    private fun workload(json: JSONObject) = GuardianWorkload(
        json.getString("id"), json.getString("name"), json.getString("desiredState"), json.getString("actualState"),
        if (json.isNull("processId")) null else json.getInt("processId"), json.getInt("restartCount"),
        json.optNullable("healthStatus"), json.optInt("healthFailureCount"), json.optNullable("executablePath"),
        json.optNullable("workingDirectory"), json.optBoolean("enabledOnBoot"), json.optNullable("runAs"),
        if (json.isNull("lastExitCode")) null else json.getInt("lastExitCode"), json.optNullable("lastProblemCode"),
    )
    fun definition(body: String): GuardianDefinition? = JSONObject(body).let { outer ->
        val json = outer.optJSONObject("definition") ?: return@let null
        val health = json.optJSONObject("healthCheck")?.let {
            GuardianHealthCheck(it.getString("type"), it.optNullable("target"), it.optInt("intervalSeconds", 15),
                it.optInt("timeoutSeconds", 5), it.optInt("failureThreshold", 3))
        }
        val args = json.optJSONArray("arguments") ?: JSONArray()
        GuardianDefinition(json.getString("id"), json.getString("name"), json.getString("executablePath"),
            List(args.length()) { args.getString(it) }, json.getString("workingDirectory"),
            json.optBoolean("enabledOnBoot"), json.optInt("stopTimeoutSeconds", 15),
            json.optInt("maxRestartAttempts", 3), health, json.optNullable("runAs"))
    }
    fun logs(body: String): List<GuardianLog> = JSONArray(body).let { logs ->
        List(logs.length()) { index -> logs.getJSONObject(index).let { GuardianLog(it.getString("timestamp"), it.getString("stream"), it.getString("message")) } }
    }
    fun operation(body: String) = JSONObject(body).let { GuardianOperation(it.getBoolean("success"), it.optString("problemCode")) }
    fun upsert(body: String) = operation(body)
    fun definitionJson(value: GuardianDefinition): String = JSONObject().apply {
            put("id", value.id); put("name", value.name); put("executablePath", value.executablePath)
            put("arguments", JSONArray(value.arguments)); put("workingDirectory", value.workingDirectory)
            put("enabledOnBoot", value.enabledOnBoot); put("stopTimeoutSeconds", value.stopTimeoutSeconds)
            put("maxRestartAttempts", value.maxRestartAttempts); put("runAs", value.runAs)
            put("healthCheck", value.healthCheck?.let { health -> JSONObject().apply {
                put("type", health.type); put("target", health.target); put("intervalSeconds", health.intervalSeconds)
                put("timeoutSeconds", health.timeoutSeconds); put("failureThreshold", health.failureThreshold)
            } })
    }.toString()
    private fun JSONObject.optNullable(name: String): String? = if (isNull(name)) null else getString(name)
}
