package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/** Read-only projections of Protocol/ApplicationDeployments. Configuration values are not retained. */
data class DeploymentApplication(
    val id: String,
    val name: String,
    val sourceKind: String,
    val workloadKind: String,
    val desiredState: String,
    val actualState: String,
    val readinessLevel: String,
    val currentRevisionNumber: Int?,
    val containerName: String?,
    val containerPort: Int,
    val hostPort: Int?,
    val bindAddress: String,
    val domain: String?,
    val driftProblemCode: String?,
)

data class DeploymentRevision(val id: String, val number: Int, val imageReference: String, val isCurrent: Boolean)

data class DeploymentOperation(
    val operationId: String,
    val kind: String,
    val state: String,
    val stage: String,
    val progress: Int?,
    val problemCode: String?,
    val recoveryProblemCode: String?,
    val createdAtMillis: Long?,
)

data class DeploymentSnapshot(
    val application: DeploymentApplication,
    val revisions: List<DeploymentRevision>,
    val operations: List<DeploymentOperation>,
    val activeOperation: DeploymentOperation?,
)

data class DeploymentRuntime(
    val isAvailable: Boolean,
    val problemCode: String,
    val serverVersion: String?,
    val operatingSystem: String?,
    val architecture: String?,
)

object ApplicationDeploymentRoutes {
    const val APPLICATIONS = "/api/v1.0/application-deployments/applications"
    const val RUNTIME = "/api/v1.0/docker/status"
    fun application(id: String): String = "$APPLICATIONS/${UUID.fromString(id)}"
}

/** Required fields fail closed; unfamiliar enum strings stay unknown in the presentation layer. */
internal object ApplicationDeploymentWire {
    fun applications(payload: String): List<DeploymentApplication> = JSONArray(payload).objects(::application)

    fun snapshot(payload: String): DeploymentSnapshot = JSONObject(payload).let { json ->
        DeploymentSnapshot(
            application(json.getJSONObject("application")),
            json.getJSONArray("revisions").objects {
                DeploymentRevision(it.getString("id"), it.getInt("number"), it.getString("imageReference"), it.getBoolean("isCurrent"))
            },
            json.getJSONArray("operations").objects(::operation),
            if (json.isNull("activeOperation")) null else operation(json.getJSONObject("activeOperation")),
        )
    }

    fun runtime(payload: String): DeploymentRuntime = JSONObject(payload).let {
        DeploymentRuntime(it.getBoolean("isAvailable"), it.getString("problemCode"),
            it.nullableText("serverVersion"), it.nullableText("operatingSystem"), it.nullableText("architecture"))
    }

    private fun application(json: JSONObject) = DeploymentApplication(
        json.getString("id"), json.getString("name"), json.getString("sourceKind"),
        json.getString("workloadKind"), json.getString("desiredState"), json.getString("actualState"),
        json.getString("readinessLevel"), json.nullableInt("currentRevisionNumber"),
        json.nullableText("containerName"), json.getInt("containerPort"), json.nullableInt("hostPort"),
        json.getString("bindAddress"), json.nullableText("domain"), json.nullableText("driftProblemCode"),
    )

    private fun operation(json: JSONObject) = DeploymentOperation(
        json.getString("operationId"), json.getString("kind"), json.getString("state"), json.getString("stage"),
        json.nullableInt("progress"), json.nullableText("problemCode"), json.nullableText("recoveryProblemCode"),
        IsoInstant.toEpochMillis(json.getString("createdAt")),
    )

    private fun JSONObject.nullableText(key: String): String? =
        if (isNull(key)) null else getString(key).takeIf { it.isNotBlank() }
    private fun JSONObject.nullableInt(key: String): Int? = if (isNull(key)) null else getInt(key)
    private fun <T> JSONArray.objects(parse: (JSONObject) -> T): List<T> =
        (0 until length()).map { parse(getJSONObject(it)) }
}
