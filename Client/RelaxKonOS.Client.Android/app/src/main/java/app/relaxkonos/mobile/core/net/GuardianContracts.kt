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
    val runAsIdentity: String?,
)
data class GuardianDefinitionResult(val success: Boolean, val problemCode: String, val definition: GuardianDefinition?)

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
    private fun encode(value: String): String { require(value.isNotBlank() && value != "." && value != ".." && '\u0000' !in value); return URLEncoder.encode(value, "UTF-8").replace("+", "%20") }
}

internal object GuardianWire {
    fun status(body: String) = JSONObject(body).let {
        GuardianStatus(it.requiredBool("isInstalled"), it.requiredBool("isRunning"), it.getString("problemCode"), it.optNullable("version"))
    }
    fun workloads(body: String): List<GuardianWorkload> = JSONArray(body).let { array ->
        List(array.length()) { index -> workload(array.getJSONObject(index)) }
    }
    private fun workload(json: JSONObject) = GuardianWorkload(
        json.getString("id"), json.getString("name"), json.getString("desiredState"), json.getString("actualState"),
        if (json.isNull("processId")) null else json.requiredInt("processId"), json.requiredInt("restartCount"),
        json.optNullable("healthStatus"), json.requiredInt("healthFailureCount"), json.optNullable("executablePath"),
        json.optNullable("workingDirectory"), json.requiredBool("enabledOnBoot"), json.optNullable("runAs"),
        if (json.isNull("lastExitCode")) null else json.requiredInt("lastExitCode"), json.optNullable("lastProblemCode"),
    )
    fun definition(body: String): GuardianDefinitionResult = JSONObject(body).let { outer ->
        val result = operation(body)
        val json = outer.optJSONObject("definition")
        if (!result.success) return@let GuardianDefinitionResult(false, result.problemCode, null)
        require(json != null) { "Successful definition response must include the current definition" }
        val healthValue = json.get("healthCheck")
        require(healthValue == JSONObject.NULL || healthValue is JSONObject)
        val health = (healthValue as? JSONObject)?.let {
            GuardianHealthCheck(it.getString("type"), it.optNullable("target"), it.requiredInt("intervalSeconds"),
                it.requiredInt("timeoutSeconds"), it.requiredInt("failureThreshold"))
        }
        val args = json.getJSONArray("arguments")
        val definition = GuardianDefinition(json.getString("id"), json.getString("name"), json.getString("executablePath"),
            List(args.length()) { require(args.get(it) is String); args.getString(it) }, json.getString("workingDirectory"),
            json.get("enabledOnBoot").let { require(it is Boolean); it }, json.requiredInt("stopTimeoutSeconds"),
            json.requiredInt("maxRestartAttempts"), health, json.optNullable("runAs"), json.optNullable("runAsIdentity"))
        GuardianDefinitionResult(true, result.problemCode, definition)
    }
    fun logs(body: String): List<GuardianLog> = JSONArray(body).let { logs ->
        List(logs.length()) { index -> logs.getJSONObject(index).let { GuardianLog(it.getString("timestamp"), it.getString("stream"), it.getString("message")) } }
    }
    fun operation(body: String) = JSONObject(body).let { GuardianOperation(it.get("success").let { value -> require(value is Boolean); value }, it.getString("problemCode")) }
    fun upsert(body: String) = operation(body)
    fun definitionJson(value: GuardianDefinition): String = JSONObject().apply {
            put("id", value.id); put("name", value.name); put("executablePath", value.executablePath)
            put("arguments", JSONArray(value.arguments)); put("workingDirectory", value.workingDirectory)
            put("enabledOnBoot", value.enabledOnBoot); put("stopTimeoutSeconds", value.stopTimeoutSeconds)
            put("maxRestartAttempts", value.maxRestartAttempts); put("runAs", value.runAs ?: JSONObject.NULL); put("runAsIdentity", value.runAsIdentity ?: JSONObject.NULL)
            put("healthCheck", value.healthCheck?.let { health -> JSONObject().apply {
                put("type", health.type); put("target", health.target ?: JSONObject.NULL); put("intervalSeconds", health.intervalSeconds)
                put("timeoutSeconds", health.timeoutSeconds); put("failureThreshold", health.failureThreshold)
            } } ?: JSONObject.NULL)
    }.toString()
    private fun JSONObject.requiredInt(name: String): Int {
        val value = get(name); require(value is Number)
        val number = value.toDouble()
        require(number.isFinite() && number % 1.0 == 0.0 && number >= Int.MIN_VALUE.toDouble() && number <= Int.MAX_VALUE.toDouble())
        return number.toInt()
    }
    private fun JSONObject.requiredBool(name: String): Boolean = get(name).let { require(it is Boolean); it }
    private fun JSONObject.optNullable(name: String): String? = get(name).let { if (it == JSONObject.NULL) null else { require(it is String); it } }
}
