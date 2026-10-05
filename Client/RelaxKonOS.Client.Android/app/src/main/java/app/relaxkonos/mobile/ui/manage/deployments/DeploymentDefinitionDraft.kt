package app.relaxkonos.mobile.ui.manage.deployments

import androidx.compose.runtime.*
import app.relaxkonos.mobile.core.net.*

/** A complete in-memory draft. No source, catalog version, runtime identity, or secret body is persisted. */
internal class DeploymentDefinitionDraft(val baseline: DeploymentApplication) {
    var name by mutableStateOf(baseline.name)
    var workload by mutableStateOf(baseline.workloadKind)
    var readiness by mutableStateOf(baseline.readinessLevel)
    var healthPath by mutableStateOf(baseline.healthCheckPath.orEmpty())
    var containerPort by mutableStateOf(baseline.containerPort.toString())
    var hostPort by mutableStateOf(baseline.hostPort?.toString().orEmpty())
    var bindAddress by mutableStateOf(baseline.bindAddress)
    var cpuCores by mutableStateOf(baseline.limits.cpuCores?.toString().orEmpty())
    // Bytes preserve definitions whose ceiling is not an integral number of MiB.
    var memoryBytes by mutableStateOf(baseline.limits.memoryBytes?.toString().orEmpty())
    var pidsLimit by mutableStateOf(baseline.limits.pidsLimit?.toString().orEmpty())
    var siteId by mutableStateOf(baseline.siteId.orEmpty())
    val volumes = mutableStateListOf<DeploymentVolume>().apply { addAll(baseline.volumes) }
    val configuration = mutableStateListOf<DeploymentDefinitionConfig>().apply { addAll(baseline.configuration) }

    fun putConfiguration(name: String, value: String, secret: Boolean): Boolean {
        val key = name.trim()
        if (!environmentName.matches(key) || value.length > 4096 || value.any(Char::isISOControl)) return false
        val existing = configuration.singleOrNull { it.name == key }
        if (existing == null && configuration.size >= 64) return false
        val retainedVersion = existing?.secretVersion?.takeIf { existing.isSecret && secret }
        if (secret && value.isEmpty() && retainedVersion == null) return false
        val entry = DeploymentDefinitionConfig(key, if (secret && value.isEmpty()) null else value, secret, retainedVersion)
        if (existing == null) configuration += entry else configuration[configuration.indexOf(existing)] = entry
        return true
    }

    fun putVolume(name: String, path: String, readOnly: Boolean): Boolean {
        val key = name.trim()
        if (!volumeName.matches(key) || !validPath(path)) return false
        val index = volumes.indexOfFirst { it.name == key }
        if (index == -1 && volumes.size >= 8) return false
        val volume = DeploymentVolume(key, path, readOnly)
        if (index == -1) volumes += volume else volumes[index] = volume
        return true
    }

    fun requestOrNull(): DeploymentDefinitionUpdate? {
        val port = containerPort.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
        val published = if (hostPort.isBlank()) null else hostPort.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
        val cpu = if (cpuCores.isBlank()) null else cpuCores.toDoubleOrNull()?.takeIf { it.isFinite() && it > 0 && it <= 64 } ?: return null
        val memory = if (memoryBytes.isBlank()) null else memoryBytes.toLongOrNull()?.takeIf { it in 16L * 1024 * 1024..64L * 1024 * 1024 * 1024 } ?: return null
        val pids = if (pidsLimit.isBlank()) null else pidsLimit.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
        val path = healthPath.trim().takeIf { readiness == "http" }
        val site = siteId.trim().ifEmpty { null }
        if (!Regex("[a-z0-9][a-z0-9_-]{2,39}").matches(name.trim()) || workload !in setOf("web", "worker") || readiness !in setOf("http", "process") ||
            bindAddress !in setOf("127.0.0.1", "0.0.0.0", "::1") ||
            readiness == "http" && (workload != "web" || published == null || path.isNullOrEmpty() || !path.startsWith('/') || path.length > 256 || path.any(Char::isISOControl)) ||
            site != null && (!Regex("[A-Za-z0-9]{1,128}").matches(site) || published == null) ||
            volumes.size > 8 || volumes.map { it.name }.distinct().size != volumes.size || volumes.any { !volumeName.matches(it.name) || !validPath(it.containerPath) } ||
            configuration.size > 64 || configuration.map { it.name }.distinct().size != configuration.size || configuration.any {
                !environmentName.matches(it.name) || if (it.isSecret) it.value?.let { value -> value.isEmpty() || value.length > 4096 || value.any(Char::isISOControl) } ?: (it.secretVersion == null)
                else it.value == null || it.value.length > 4096 || it.value.any(Char::isISOControl) || it.secretVersion != null
            }) return null
        return DeploymentDefinitionUpdate(name.trim(), workload, readiness, baseline.updatedAt, path, port, published, bindAddress,
            DeploymentLimits(cpu, memory, pids), volumes.toList(), configuration.toList(), site)
    }

    private fun validPath(path: String): Boolean = path.length in 2..256 && path.startsWith('/') && !path.any(Char::isISOControl) && path.split('/').none { it == "." || it == ".." }
    private companion object {
        val environmentName = Regex("[A-Za-z_][A-Za-z0-9_]{0,63}")
        val volumeName = Regex("[a-z][a-z0-9_-]{0,31}")
    }
}
