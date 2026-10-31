package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

/** Current shared ServerModuleDto. There is no legacy enabled-field parser. */
data class ServerModuleFact(
    val id: String, val serviceId: String, val effectiveEnabled: Boolean, val desiredEnabled: Boolean,
    val restartRequired: Boolean, val desiredRevision: String, val processGeneration: String,
    val installedVersion: String?, val residencyState: String?, val strategy: String?, val activeLeases: Int?,
    val requires: List<String>, val requiredBy: List<String>, val blockedBy: String?, val failure: String?,
    val allowedActions: Set<String> = emptySet(),
    val deniedActions: List<String> = emptyList(),
    val recoveryState: String = "none",
    val observedAt: String? = null,
    val lastUsedAt: String? = null,
    val lastReleasedAt: String? = null,
    val lastWakeMilliseconds: Double? = null,
    val desiredPolicy: ServerModuleResidencyPolicy? = null,
    val effectivePolicy: ServerModuleResidencyPolicy? = null,
    val capabilities: List<String> = emptyList(),
)
data class ServerModuleResidencyPolicy(val strategy: String, val idleGraceSeconds: Int)

data class ServerModulePlan(val id: String, val serviceId: String, val expiresAt: String,
    val added: List<String>, val removed: List<String>, val blockers: List<String>, val raw: String,
    val action: String, val moduleIds: List<String>, val restartRequired: Boolean,
    val policy: ServerModuleResidencyPolicy? = null)
data class ServerModuleOperation(val id: String, val planId: String, val serviceId: String, val state: String,
    val enabledIds: List<String>, val problemCode: String?, val moduleResults: List<ServerModuleActionOutcome> = emptyList()) {
    val terminal: Boolean get() = state in setOf("Succeeded", "Failed")
}
data class ServerModuleActionOutcome(val moduleId: String, val succeeded: Boolean, val problemCode: String?)
data class ServerModulePlanStatus(val plan: ServerModulePlan, val operation: ServerModuleOperation?)

object ServerModulesWire {
    const val ROOT = "/api/v1.0/server/modules"
    const val PLANS = "/api/v1.0/server/module-plans"
    private fun uuid(id: String): String { require(java.util.UUID.fromString(id).toString() == id.lowercase()); return id }
    fun planRoute(id: String): String = "$PLANS/${uuid(id)}"
    private fun strings(values: JSONArray): List<String> {
        require(values.length() <= 128)
        return (0 until values.length()).map { values.get(it) as? String ?: error("Invalid string") }
    }
    private fun policy(value: JSONObject): ServerModuleResidencyPolicy {
        val strategy = value.getString("strategy")
        val grace = value.get("idleGraceSeconds") as? Int ?: error("Invalid idleGraceSeconds")
        require(strategy in setOf("KeepLoaded", "IdleDelayed", "ReleaseOnLogout") && grace in 0..604800)
        return ServerModuleResidencyPolicy(strategy, grace)
    }
    fun plan(payload: String): ServerModulePlan {
        val value = JSONObject(payload)
        val blockers = value.getJSONArray("blockedBy")
        require(blockers.length() <= 256)
        val id = value.getString("planId"); planRoute(id)
        val service = value.getString("serviceId")
        val target = value.getJSONObject("target")
        require(target.getString("serviceId") == service)
        require(service.isNotBlank() && target.getString("expectedRevision").isNotBlank()
            && value.getString("expectedRevision") == target.getString("expectedRevision")
            && target.getString("idempotencyKey").length in 1..128)
        require(target.getString("action") in setOf("Enable", "Disable", "SetResidencyPolicy", "Wake", "TrySleep", "Install", "Update", "Repair", "Rollback", "Remove"))
        require(strings(target.getJSONArray("moduleIds")).isNotEmpty())
        require(strings(target.getJSONArray("enabledModuleIds")).distinct().size == target.getJSONArray("enabledModuleIds").length())
        java.time.OffsetDateTime.parse(value.getString("expiresAt"))
        return ServerModulePlan(id, service, value.getString("expiresAt"), strings(value.getJSONArray("added")),
            strings(value.getJSONArray("removed")), (0 until blockers.length()).map { blockers.getJSONObject(it).getString("problemCode") }, value.toString(),
            target.getString("action"), strings(target.getJSONArray("moduleIds")), value.get("restartRequired") as? Boolean ?: error("Invalid restartRequired"),
            if (target.getString("action") == "SetResidencyPolicy") policy(target) else null)
    }
    fun operation(payload: String): ServerModuleOperation {
        val value = JSONObject(payload)
        val state = value.getString("state")
        require(state in setOf("Prepared", "Applying", "AwaitingRestart", "Verifying", "Succeeded", "Failed", "RecoveryRequired"))
        uuid(value.getString("operationId")); uuid(value.getString("planId"))
        java.time.OffsetDateTime.parse(value.getString("createdAt")); java.time.OffsetDateTime.parse(value.getString("updatedAt"))
        require(value.getString("desiredRevision").isNotBlank())
        val operation = ServerModuleOperation(value.getString("operationId"), value.getString("planId"), value.getString("serviceId"), state,
            strings(value.getJSONArray("enabledModuleIds")), value.optString("problemCode").takeUnless { it.isEmpty() || it == "null" },
            value.optJSONArray("moduleResults")?.let { outcomes ->
                require(outcomes.length() <= 128)
                (0 until outcomes.length()).map { index -> val outcome = outcomes.getJSONObject(index)
                    ServerModuleActionOutcome(outcome.getString("moduleId"), outcome.get("succeeded") as? Boolean ?: error("Invalid succeeded"),
                        outcome.optString("problemCode").takeUnless { it.isEmpty() || it == "null" }) }
            } ?: emptyList())
        require(operation.serviceId.isNotBlank() && operation.enabledIds.distinct().size == operation.enabledIds.size
            && operation.moduleResults.map { it.moduleId }.distinct().size == operation.moduleResults.size)
        require(state != "Succeeded" || operation.problemCode == null && operation.moduleResults.all { it.succeeded })
        return operation
    }
    fun status(payload: String): ServerModulePlanStatus = JSONObject(payload).let { value ->
        val plan = plan(value.getJSONObject("plan").toString())
        val operation = value.optJSONObject("operation")?.let { operation(it.toString()) }
        require(operation == null || operation.planId == plan.id && operation.serviceId == plan.serviceId)
        ServerModulePlanStatus(plan, operation)
    }
    fun facts(payload: String): List<ServerModuleFact> {
        val rows = JSONArray(payload)
        require(rows.length() <= 128)
        val result = (0 until rows.length()).map { index ->
            val row = rows.getJSONObject(index)
            fun strings(name: String): List<String> = row.getJSONArray(name).let { values ->
                require(values.length() <= 128)
                (0 until values.length()).map { values.getString(it) }
            }
            fun boolean(name: String): Boolean = (row.get(name) as? Boolean) ?: error("Invalid $name")
            val residency = row.optJSONObject("residency")
            ServerModuleFact(row.getString("id"), row.getString("serviceId"), boolean("effectiveEnabled"),
                boolean("desiredEnabled"), boolean("restartRequired"), row.getString("desiredRevision"),
                row.getString("processGeneration"), row.optString("installedVersion").takeUnless { it == "null" || it.isEmpty() },
                residency?.getString("state"), residency?.getString("strategy"), residency?.getInt("activeLeases"),
                strings("requires"), strings("requiredBy"), residency?.optString("blockedBy")?.takeUnless { it == "null" },
                residency?.optString("failure")?.takeUnless { it == "null" },
                row.optJSONArray("allowedActions")?.let { actions ->
                    require(actions.length() <= 16)
                    (0 until actions.length()).map { actions.getJSONObject(it) }.filter { it.get("allowed") == true }
                        .map { it.getString("action") }.toSet()
                } ?: emptySet(),
                row.optJSONArray("allowedActions")?.let { actions ->
                    (0 until actions.length()).map { actions.getJSONObject(it) }.filter { it.get("allowed") == false }
                        .flatMap { action -> val reasons = action.getJSONArray("blockedBy")
                            require(reasons.length() <= 256)
                            (0 until reasons.length()).map { action.getString("action") + ": " + reasons.getJSONObject(it).getString("problemCode") } }
                } ?: emptyList(), row.optString("recoveryState", "none"), row.optString("observedAt").takeUnless { it == "null" || it.isEmpty() },
                residency?.optString("lastUsedAt")?.takeUnless { it == "null" }, residency?.optString("lastReleasedAt")?.takeUnless { it == "null" },
                if (residency == null || residency.isNull("lastWakeMilliseconds")) null else residency.getDouble("lastWakeMilliseconds"),
                row.optJSONObject("desiredResidencyPolicy")?.let(::policy), row.optJSONObject("effectiveResidencyPolicy")?.let(::policy), strings("capabilities"))
        }
        require(result.map { it.id }.distinct().size == result.size)
        require(result.map { it.serviceId }.distinct().size <= 1)
        return result
    }
}
