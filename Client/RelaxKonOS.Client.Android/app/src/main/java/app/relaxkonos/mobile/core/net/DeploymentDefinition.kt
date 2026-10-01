package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

/** A response contains a secret's version only; a newly entered value lives in an ephemeral request. */
data class DeploymentDefinitionConfig(val name: String, val value: String?, val isSecret: Boolean, val secretVersion: Int?) {
    override fun toString(): String = "DeploymentDefinitionConfig(name=$name, value=${if (isSecret) "[redacted]" else value}, isSecret=$isSecret, secretVersion=$secretVersion)"
}

/** Replaces the complete definition. Source and catalog identity cannot be edited here. */
data class DeploymentDefinitionUpdate(
    val name: String,
    val workloadKind: String,
    val readinessLevel: String,
    val expectedUpdatedAt: String,
    val healthCheckPath: String?,
    val containerPort: Int,
    val hostPort: Int?,
    val bindAddress: String,
    val limits: DeploymentLimits,
    val volumes: List<DeploymentVolume>,
    val configuration: List<DeploymentDefinitionConfig>,
    val siteId: String?,
)

internal object DeploymentDefinitionWire {
    fun limits(json: JSONObject): DeploymentLimits = DeploymentLimits(
        nullable(json, "cpuCores") { number -> (number as? Number)?.toDouble()?.also { require(it.isFinite() && it > 0 && it <= 64) } ?: error("CPU") },
        nullable(json, "memoryBytes") { integer(it).also { value -> require(value in 16L * 1024 * 1024..64L * 1024 * 1024 * 1024) } },
        nullable(json, "pidsLimit") { integer(it).also { value -> require(value in 1..65535) }.toInt() },
    )

    fun volumes(array: JSONArray): List<DeploymentVolume> = objects(array, 8) { json ->
        DeploymentVolume(text(json, "name"), text(json, "containerPath"), json.get("readOnly") as? Boolean ?: error("readOnly"))
    }.also { entries -> require(entries.map { it.name }.distinct().size == entries.size) }

    fun configuration(array: JSONArray): List<DeploymentDefinitionConfig> = objects(array, 64) { json ->
        val name = text(json, "name")
        val secret = json.get("isSecret") as? Boolean ?: error("isSecret")
        val value = nullable(json, "value") { it as? String ?: error("value") }
        val version = nullable(json, "secretVersion") { integer(it).also { n -> require(n in 1..Int.MAX_VALUE) }.toInt() }
        require(Regex("[A-Za-z_][A-Za-z0-9_]{0,63}").matches(name))
        require(if (secret) value == null && version != null else value != null && value.length <= 4096 && version == null)
        DeploymentDefinitionConfig(name, value, secret, version)
    }.also { entries -> require(entries.map { it.name }.distinct().size == entries.size) }

    fun body(definition: DeploymentDefinitionUpdate): JsonBody = JsonBody()
        .string("name", definition.name).string("workloadKind", definition.workloadKind).string("readinessLevel", definition.readinessLevel)
        .string("expectedUpdatedAt", definition.expectedUpdatedAt).raw("healthCheckPath", definition.healthCheckPath?.let(JSONObject::quote) ?: "null")
        .int("containerPort", definition.containerPort).raw("hostPort", definition.hostPort?.toString() ?: "null")
        .string("bindAddress", definition.bindAddress).raw("siteId", definition.siteId?.let(JSONObject::quote) ?: "null")
        .raw("limits", JSONObject().put("cpuCores", definition.limits.cpuCores ?: JSONObject.NULL)
            .put("memoryBytes", definition.limits.memoryBytes ?: JSONObject.NULL).put("pidsLimit", definition.limits.pidsLimit ?: JSONObject.NULL).toString())
        .raw("volumes", JSONArray().apply { definition.volumes.forEach { volume -> put(JSONObject().put("name", volume.name)
            .put("containerPath", volume.containerPath).put("readOnly", volume.readOnly)) } }.toString())
        .raw("configuration", JSONArray().apply { definition.configuration.forEach { config -> put(JSONObject().put("name", config.name)
            .put("value", config.value ?: JSONObject.NULL).put("isSecret", config.isSecret).put("secretVersion", config.secretVersion ?: JSONObject.NULL)) } }.toString())

    private fun text(json: JSONObject, key: String): String = json.get(key) as? String ?: error(key)
    private fun integer(value: Any): Long = when (value) { is Int -> value.toLong(); is Long -> value; else -> error("integer") }
    private fun <T> nullable(json: JSONObject, key: String, parse: (Any) -> T): T? {
        require(json.has(key)) { "Missing $key" }
        return if (json.isNull(key)) null else parse(json.get(key))
    }
    private fun <T> objects(array: JSONArray, maximum: Int, parse: (JSONObject) -> T): List<T> {
        require(array.length() <= maximum)
        return (0 until array.length()).map { parse(array.getJSONObject(it)) }
    }
}
