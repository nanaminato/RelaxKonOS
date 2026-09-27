package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.relaxkonos.mobile.core.net.ArchiveDeploymentDefinition
import app.relaxkonos.mobile.core.net.DeploymentConfigEntry
import app.relaxkonos.mobile.core.net.DeploymentTemplate

/**
 * Transient form model for the application-deployment sheet.
 *
 * Validation and request shaping are kept outside Compose so the sheet only renders fields and routes
 * events. This object is deliberately ephemeral: configuration secrets remain in memory only until the
 * user submits or dismisses the sheet.
 */
class DeploymentCreateForm(private val templates: List<DeploymentTemplate>) {
    var sourceKind by mutableStateOf(templates.firstOrNull()?.sourceKind.orEmpty())
        private set
    var name by mutableStateOf("")
    var image by mutableStateOf("")
    var port by mutableStateOf(templates.firstOrNull()?.defaultContainerPort?.toString() ?: "8080")
    var workload by mutableStateOf("Web")
    var baseImage by mutableStateOf("")
    var runtimeVersion by mutableStateOf("")
    var programEntry by mutableStateOf("")
    var selfContained by mutableStateOf(false)
    val configuration = mutableStateListOf<DeploymentConfigEntry>()
    var configurationName by mutableStateOf("")
    var configurationValue by mutableStateOf("")
    var configurationSecret by mutableStateOf(false)

    val template: DeploymentTemplate? get() = templates.firstOrNull { it.sourceKind == sourceKind }
    val parsedPort: Int? get() = port.toIntOrNull()?.takeIf { it in 1..65535 }
    val isArchive: Boolean get() = template?.requiresArchive == true
    val canSubmit: Boolean get() = name.isNotBlank() && parsedPort != null && when {
        template == null -> false
        template.requiresImageReference -> image.isNotBlank()
        template.sourceKind == "PythonProject" -> programEntry.isNotBlank()
        else -> true
    }

    fun selectSource(value: String) {
        sourceKind = value
        val selected = template
        port = selected?.defaultContainerPort?.toString() ?: "8080"
        baseImage = selected?.defaultBaseImage.orEmpty()
        runtimeVersion = ""
        programEntry = ""
        selfContained = false
    }

    fun addConfiguration() {
        if (configurationName.isBlank()) return
        configuration.removeAll { it.name == configurationName.trim() }
        configuration += DeploymentConfigEntry(configurationName.trim(), configurationValue, configurationSecret)
        configurationName = ""
        configurationValue = ""
        configurationSecret = false
    }

    fun imageConfiguration(): List<DeploymentConfigEntry> = configuration.toList()

    fun archiveDefinition(): ArchiveDeploymentDefinition? {
        val selected = template ?: return null
        val selectedPort = parsedPort ?: return null
        return ArchiveDeploymentDefinition(
            sourceKind = selected.sourceKind,
            name = name.trim(),
            containerPort = selectedPort,
            workloadKind = workload,
            readinessLevel = if (workload == "Web") "Http" else "Process",
            healthCheckPath = if (workload == "Web") "/" else null,
            baseImage = baseImage.trim().ifBlank { null },
            runtimeVersion = runtimeVersion.trim().ifBlank { null },
            programEntry = programEntry.trim().ifBlank { null },
            selfContained = selfContained,
            configuration = configuration.toList(),
        )
    }
}
