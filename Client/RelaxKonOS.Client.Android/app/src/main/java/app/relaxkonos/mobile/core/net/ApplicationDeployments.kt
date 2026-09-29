package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID
import java.util.Locale

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
    /** Server-authoritative association with a RelaxKonOS-owned reverse-proxy site. */
    val siteId: String?,
    val domain: String?,
    val driftProblemCode: String?,
    /** The exact product template used to create this instance, if any. */
    val catalogTemplateId: String? = null,
    val catalogTemplateVersion: String? = null,
)

data class DeploymentRevision(val id: String, val number: Int, val imageReference: String, val isCurrent: Boolean)

/** Bounded, server-sanitized workload output. Android never receives an unbounded log stream here. */
data class DeploymentLog(val lines: List<String>, val truncated: Boolean)
data class DeploymentOperationDiagnostics(val operationId: String, val lines: List<String>, val truncated: Boolean)

data class DeploymentOperation(
    val operationId: String,
    val applicationId: String,
    val kind: String,
    val state: String,
    val stage: String,
    val progress: Int?,
    val problemCode: String?,
    val recoveryProblemCode: String?,
    val createdAtMillis: Long?,
    val cancellable: Boolean,
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

/** Server-owned template metadata. Android consumes this rather than copying runtime defaults. */
data class DeploymentTemplate(
    val sourceKind: String,
    val displayName: String,
    val defaultBaseImage: String?,
    val requiresArchive: Boolean,
    val requiresImageReference: Boolean,
    val supportsSelfContained: Boolean,
    val defaultContainerPort: Int,
)

data class DeploymentImageTag(val tag: String, val imageReference: String)
data class DeploymentImageTags(val available: Boolean, val tags: List<DeploymentImageTag>)

/** Product application catalogue, distinct from the AD02 runtime-source templates above. */
data class CatalogTemplate(
    val schemaVersion: String,
    val id: String,
    val version: String,
    val publisher: String,
    val source: String,
    val trusted: Boolean,
    val purpose: String,
    val description: String,
    val supportedPlatforms: List<String>,
    val requiredCapabilities: List<String>,
    val minimumResources: CatalogResources,
    val fields: List<CatalogField>,
    val volumes: List<CatalogVolume>,
    val containerPort: Int,
    val accessPath: String?,
    val maintenanceNotes: String,
    val withdrawn: Boolean,
)

data class CatalogResources(val cpuCores: Double?, val memoryBytes: Long?, val pidsLimit: Int?)

data class CatalogField(
    val id: String,
    val type: String,
    val required: Boolean,
    val defaultValue: String?,
    val options: List<String>,
    val labels: Map<String, String>,
    val help: String?,
) {
    fun label(): String = labels[Locale.getDefault().language] ?: labels["en"] ?: id
}
data class CatalogVolume(val name: String, val containerPath: String)
data class CatalogFieldValue(val id: String, val value: String)

/**
 * All local checks are explanatory only; the server repeats every check before it creates a
 * definition.  Unknown catalogue input fails closed instead of being rendered as a text field.
 */
data class CatalogInstallCompatibility(val blockers: Set<CatalogInstallBlocker>) {
    val canInstall: Boolean get() = blockers.isEmpty()
}

enum class CatalogInstallBlocker {
    UnsupportedSchema,
    UntrustedSource,
    Withdrawn,
    MissingCapability,
    RuntimeUnavailable,
    UnsupportedPlatform,
    InvalidField,
}

fun CatalogTemplate.compatibility(
    capabilities: Set<String>,
    runtime: DeploymentRuntime?,
): CatalogInstallCompatibility {
    val blockers = buildSet {
        if (schemaVersion != "1") add(CatalogInstallBlocker.UnsupportedSchema)
        if (!trusted) add(CatalogInstallBlocker.UntrustedSource)
        if (withdrawn) add(CatalogInstallBlocker.Withdrawn)
        if (!requiredCapabilities.all(capabilities::contains)) add(CatalogInstallBlocker.MissingCapability)
        if (!hasValidFields()) add(CatalogInstallBlocker.InvalidField)
        if (runtime?.isAvailable != true) {
            add(CatalogInstallBlocker.RuntimeUnavailable)
        } else if (!supports(runtime)) {
            add(CatalogInstallBlocker.UnsupportedPlatform)
        }
    }
    return CatalogInstallCompatibility(blockers)
}

private fun CatalogTemplate.hasValidFields(): Boolean =
    fields.map(CatalogField::id).toSet().size == fields.size && fields.all { field ->
        field.id.isNotBlank() && field.type in setOf("text", "number", "enum", "secret") &&
            (field.type != "enum" || field.options.isNotEmpty()) &&
            (field.defaultValue == null || field.type != "secret") &&
            (field.defaultValue == null || field.type != "enum" || field.defaultValue in field.options)
    }

private fun CatalogTemplate.supports(runtime: DeploymentRuntime): Boolean {
    val operatingSystem = runtime.operatingSystem?.trim()?.lowercase() ?: return false
    val architecture = when (runtime.architecture?.trim()?.lowercase()) {
        "amd64", "x86_64", "x64" -> "amd64"
        "arm64", "aarch64" -> "arm64"
        "arm", "armv7l", "armhf", "arm/v7" -> "arm"
        else -> return false
    }
    return "$operatingSystem/$architecture" in supportedPlatforms
}

/** A staged archive reference; its server-side expiry and bytes never become a deployment secret. */
data class DeploymentArchive(val referenceId: String, val fileName: String, val length: Long, val expiresAtMillis: Long?)

/** A constrained configuration entry. A secret value is sent only for deployment creation and never retained in snapshots. */
data class DeploymentConfigEntry(val name: String, val value: String, val isSecret: Boolean)

data class DeploymentVolume(val name: String, val containerPath: String, val readOnly: Boolean)
data class DeploymentLimits(val cpuCores: Double?, val memoryBytes: Long?, val pidsLimit: Int?)

/** The definition fields shared by image and archive deployments. */
data class ImageDeploymentDefinition(
    val name: String,
    val containerPort: Int,
    val hostPort: Int? = null,
    val bindAddress: String = "127.0.0.1",
    val healthCheckPath: String = "/",
    val configuration: List<DeploymentConfigEntry> = emptyList(),
    val workloadKind: String = "web",
    val readinessLevel: String = "http",
    val limits: DeploymentLimits? = null,
    val volumes: List<DeploymentVolume> = emptyList(),
    val siteId: String? = null,
)

/** Archive input and the same application definition fields as the image path. */
data class ArchiveDeploymentDefinition(
    val sourceKind: String,
    val name: String,
    val containerPort: Int,
    val workloadKind: String,
    val readinessLevel: String,
    val healthCheckPath: String?,
    val baseImage: String? = null,
    val runtimeVersion: String? = null,
    val programEntry: String? = null,
    val selfContained: Boolean = false,
    val configuration: List<DeploymentConfigEntry> = emptyList(),
    val hostPort: Int? = null,
    val bindAddress: String = "127.0.0.1",
    val limits: DeploymentLimits? = null,
    val volumes: List<DeploymentVolume> = emptyList(),
    val siteId: String? = null,
    val arguments: List<String> = emptyList(),
)

/** Closed set mirroring the server lifecycle routes; callers cannot submit arbitrary action paths. */
enum class DeploymentLifecycleAction { Start, Stop, Restart }

object ApplicationDeploymentRoutes {
    const val APPLICATIONS = "/api/v1.0/application-deployments/applications"
    const val RUNTIME = "/api/v1.0/docker/status"
    const val TEMPLATES = "/api/v1.0/application-deployments/templates"
    const val IMAGE_TAGS = "/api/v1.0/application-deployments/image-tags"
    const val CATALOG = "/api/v1.0/application-deployments/catalog"
    const val CATALOG_INSTALL = "$CATALOG/install"
    const val UPLOADS = "/api/v1.0/application-deployments/uploads"
    const val FILE_REFERENCES = "/api/v1.0/application-deployments/file-references"
    fun application(id: String): String = "$APPLICATIONS/${UUID.fromString(id)}"
    fun deploy(id: String): String = "${application(id)}/deploy"
    fun rollback(id: String): String = "${application(id)}/rollback"
    fun delete(id: String): String = application(id)
    fun lifecycle(id: String, action: DeploymentLifecycleAction): String = "${application(id)}/${action.name.lowercase()}"
    fun logs(id: String, tail: Int): String = "${application(id)}/logs?tail=${tail.coerceIn(1, 1_000)}"
    fun cancelOperation(id: String): String = "/api/v1.0/application-deployments/operations/${UUID.fromString(id)}/cancel"
    fun operationLogs(id: String): String = "/api/v1.0/application-deployments/operations/${UUID.fromString(id)}/logs"
    fun operation(id: String): String = "/api/v1.0/application-deployments/operations/${UUID.fromString(id)}"
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

    fun templates(payload: String): List<DeploymentTemplate> = JSONArray(payload).objects { json ->
        DeploymentTemplate(
            json.getString("sourceKind"), json.getString("displayName"), json.nullableText("defaultBaseImage"),
            json.getBoolean("requiresArchive"), json.getBoolean("requiresImageReference"),
            json.getBoolean("supportsSelfContained"), json.getInt("defaultContainerPort"),
        )
    }

    fun imageTags(payload: String): DeploymentImageTags = JSONObject(payload).let { json ->
        DeploymentImageTags(json.getBoolean("available"), json.getJSONArray("tags").objects {
            DeploymentImageTag(it.getString("tag"), it.getString("imageReference"))
        })
    }

    fun catalog(payload: String): List<CatalogTemplate> = JSONArray(payload).objects { json ->
        CatalogTemplate(
            json.getString("schemaVersion"), json.getString("id"), json.getString("version"), json.getString("publisher"),
            json.getString("source"), json.getBoolean("trusted"), json.getString("purpose"), json.getString("description"),
            json.getJSONArray("supportedPlatforms").strings(), json.getJSONArray("requiredCapabilities").strings(),
            json.getJSONObject("minimumResources").let { resources -> CatalogResources(
                if (resources.isNull("cpuCores")) null else resources.getDouble("cpuCores"),
                if (resources.isNull("memoryBytes")) null else resources.getLong("memoryBytes"),
                if (resources.isNull("pidsLimit")) null else resources.getInt("pidsLimit"),
            ) },
            json.getJSONArray("fields").objects { field -> CatalogField(field.getString("id"), field.getString("type"), field.getBoolean("required"),
                field.nullableText("defaultValue"), field.getJSONArray("options").strings(), field.getJSONObject("labels").let { labels ->
                    mapOf("en" to labels.getString("en"), "zh" to labels.getString("zh"), "ja" to labels.getString("ja"))
                }, field.nullableText("help")) },
            json.getJSONArray("volumes").objects { volume -> CatalogVolume(volume.getString("name"), volume.getString("containerPath")) },
            json.getInt("containerPort"), json.nullableText("accessPath"), json.getString("maintenanceNotes"), json.getBoolean("withdrawn"),
        )
    }

    fun catalogInstall(payload: String): DeploymentOperation = operation(JSONObject(payload).getJSONObject("operation"))

    fun archive(payload: String): DeploymentArchive = JSONObject(payload).let { json ->
        DeploymentArchive(json.getString("referenceId"), json.getString("fileName"), json.getLong("length"),
            IsoInstant.toEpochMillis(json.getString("expiresAt")))
    }

    fun createdApplication(payload: String): DeploymentApplication = application(JSONObject(payload))

    fun acceptedOperation(payload: String): DeploymentOperation = operation(JSONObject(payload))

    fun logs(payload: String): DeploymentLog = JSONObject(payload).let { json ->
        val lines = json.getJSONArray("lines").let { array -> (0 until array.length()).map(array::getString) }
        DeploymentLog(lines, json.getBoolean("truncated"))
    }

    fun operationDiagnostics(payload: String): DeploymentOperationDiagnostics = JSONObject(payload).let { json ->
        DeploymentOperationDiagnostics(json.getString("operationId"), json.getJSONArray("lines").strings(), json.getBoolean("truncated"))
    }

    private fun application(json: JSONObject) = DeploymentApplication(
        json.getString("id"), json.getString("name"), json.getString("sourceKind"),
        json.getString("workloadKind"), json.getString("desiredState"), json.getString("actualState"),
        json.getString("readinessLevel"), json.nullableInt("currentRevisionNumber"),
        json.nullableText("containerName"), json.getInt("containerPort"), json.nullableInt("hostPort"),
        json.getString("bindAddress"), json.nullableText("siteId"), json.nullableText("domain"), json.nullableText("driftProblemCode"),
        json.nullableText("catalogTemplateId"), json.nullableText("catalogTemplateVersion"),
    )

    private fun operation(json: JSONObject) = DeploymentOperation(
        json.getString("operationId"), json.getString("applicationId"), json.getString("kind"), json.getString("state"), json.getString("stage"),
        json.nullableInt("progress"), json.nullableText("problemCode"), json.nullableText("recoveryProblemCode"),
        IsoInstant.toEpochMillis(json.getString("createdAt")), json.getBoolean("cancellable"),
    )

    private fun JSONObject.nullableText(key: String): String? =
        if (isNull(key)) null else getString(key).takeIf { it.isNotBlank() }
    private fun JSONObject.nullableInt(key: String): Int? = if (isNull(key)) null else getInt(key)
    private fun <T> JSONArray.objects(parse: (JSONObject) -> T): List<T> =
        (0 until length()).map { parse(getJSONObject(it)) }
    private fun JSONArray.strings(): List<String> = (0 until length()).map(::getString)
}
