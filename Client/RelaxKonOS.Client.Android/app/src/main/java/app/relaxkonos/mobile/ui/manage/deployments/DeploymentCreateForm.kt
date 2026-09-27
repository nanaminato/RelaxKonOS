package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.relaxkonos.mobile.core.net.*

/** Ephemeral seven-step counterpart of the desktop application deployment wizard. */
class DeploymentCreateForm(private val templates: List<DeploymentTemplate>) {
    var step by mutableStateOf(0)
        private set
    var sourceKind by mutableStateOf(templates.firstOrNull()?.sourceKind.orEmpty())
        private set
    var name by mutableStateOf("")
    var image by mutableStateOf("")
    var port by mutableStateOf(templates.firstOrNull()?.defaultContainerPort?.toString() ?: "8080")
    var hostPort by mutableStateOf("")
    var bindAddress by mutableStateOf("127.0.0.1")
    var workload by mutableStateOf("web")
    var readiness by mutableStateOf("http")
    var healthPath by mutableStateOf("/")
    var baseImage by mutableStateOf(templates.firstOrNull()?.defaultBaseImage.orEmpty())
    var runtimeVersion by mutableStateOf("")
    var programEntry by mutableStateOf("")
    var arguments by mutableStateOf("")
    var selfContained by mutableStateOf(false)
    var volumes by mutableStateOf("")
    var cpuCores by mutableStateOf("")
    var memoryMegabytes by mutableStateOf("")
    var pidsLimit by mutableStateOf("")
    var siteId by mutableStateOf("")
    var deployNow by mutableStateOf(true)
    var archiveName by mutableStateOf("")
    val configuration = mutableStateListOf<DeploymentConfigEntry>()
    var configurationName by mutableStateOf("")
    var configurationValue by mutableStateOf("")
    var configurationSecret by mutableStateOf(false)

    val template: DeploymentTemplate? get() = templates.firstOrNull { it.sourceKind == sourceKind }
    val isArchive: Boolean get() = template?.requiresArchive == true
    val parsedPort: Int? get() = port.toIntOrNull()?.takeIf { it in 1..65535 }
    val parsedHostPort: Int? get() = hostPort.toIntOrNull()?.takeIf { it in 1..65535 }
    val parsedVolumes: List<DeploymentVolume>? get() {
        val lines = lines(volumes)
        val parsed = lines.map { line ->
            val parts = line.split(':').map(String::trim)
            if (parts.size !in 2..3 || parts[0].isBlank() || !parts[1].startsWith('/') ||
                (parts.size == 3 && !parts[2].equals("ro", ignoreCase = true))) return null
            DeploymentVolume(parts[0], parts[1], parts.size == 3)
        }
        return parsed
    }
    val parsedLimits: DeploymentLimits? get() = if (cpuCores.isBlank() && memoryMegabytes.isBlank() && pidsLimit.isBlank()) null
        else DeploymentLimits(cpuCores.toDoubleOrNull(), memoryMegabytes.toLongOrNull()?.times(1024L * 1024L), pidsLimit.toIntOrNull())

    fun selectSource(value: String) {
        sourceKind = value
        val selected = template
        port = selected?.defaultContainerPort?.toString() ?: "8080"
        baseImage = selected?.defaultBaseImage.orEmpty()
        runtimeVersion = ""
        programEntry = ""
        arguments = ""
        selfContained = false
        archiveName = ""
    }

    fun selectWorkload(value: String) {
        workload = value
        readiness = if (value == "web") "http" else "process"
    }

    fun addConfiguration() {
        val key = configurationName.trim()
        if (key.isBlank()) return
        configuration.removeAll { it.name == key }
        configuration += DeploymentConfigEntry(key, configurationValue, configurationSecret)
        configurationName = ""
        configurationValue = ""
        configurationSecret = false
    }

    fun problemAt(index: Int): String? = when (index) {
        0 -> when {
            template == null -> "source"
            !Regex("^[A-Za-z0-9][a-z0-9_-]{2,39}$").matches(name.trim()) -> "name"
            else -> null
        }
        1 -> when {
            template?.requiresImageReference == true && (image.isBlank() || !hasExplicitImageVersion(image.trim())) -> "image"
            isArchive && archiveName.isBlank() -> "archive"
            isArchive && sourceKind == "pythonProject" && programEntry.isBlank() -> "entry"
            runtimeVersion.length > 64 -> "runtime"
            lines(arguments).any { it.length > 4096 } -> "arguments"
            else -> null
        }
        2 -> when {
            parsedPort == null -> "containerPort"
            hostPort.isNotBlank() && parsedHostPort == null -> "hostPort"
            readiness == "http" && workload == "web" && parsedHostPort == null -> "hostPort"
            readiness == "http" && !healthPath.startsWith('/') -> "healthPath"
            else -> null
        }
        3 -> when {
            configurationName.isNotBlank() || configurationValue.isNotBlank() -> "configuration"
            parsedVolumes == null -> "volumes"
            cpuCores.isNotBlank() && cpuCores.toDoubleOrNull()?.let { it > 0 && it <= 64 } != true -> "cpu"
            memoryMegabytes.isNotBlank() && memoryMegabytes.toLongOrNull()?.let { it in 16..65536 } != true -> "memory"
            pidsLimit.isNotBlank() && pidsLimit.toIntOrNull()?.let { it in 1..65535 } != true -> "pids"
            configuration.any { it.name.isBlank() } -> "configuration"
            else -> null
        }
        4 -> when {
            siteId.isNotBlank() && (!Regex("^[A-Za-z0-9]{1,128}$").matches(siteId) || parsedHostPort == null) -> "site"
            else -> null
        }
        else -> null
    }

    fun next(): Boolean {
        if (problemAt(step) != null || step >= 5) return false
        step++
        return true
    }

    fun back() { if (step in 1..5) step-- }
    fun goTo(index: Int) { if (index in 0..5) step = index }
    fun showProgress() { step = 6 }

    fun imageDefinition() = ImageDeploymentDefinition(
        name.trim(), parsedPort!!, parsedHostPort, bindAddress.trim(), healthPath.trim(), configuration.toList(),
        workload, readiness, parsedLimits, parsedVolumes.orEmpty(), siteId.trim().ifBlank { null },
    )

    fun archiveDefinition() = ArchiveDeploymentDefinition(
        sourceKind, name.trim(), parsedPort!!, workload, readiness,
        healthPath.trim().takeIf { readiness == "http" }, baseImage.trim().ifBlank { null },
        runtimeVersion.trim().ifBlank { null }, programEntry.trim().ifBlank { null }, selfContained,
        configuration.toList(), parsedHostPort, bindAddress.trim(), parsedLimits, parsedVolumes.orEmpty(),
        siteId.trim().ifBlank { null }, lines(arguments),
    )

    private fun hasExplicitImageVersion(reference: String): Boolean = '@' in reference || ':' in reference.substringAfterLast('/')
    private fun lines(value: String): List<String> = value.lines().map(String::trim).filter(String::isNotEmpty)
}
