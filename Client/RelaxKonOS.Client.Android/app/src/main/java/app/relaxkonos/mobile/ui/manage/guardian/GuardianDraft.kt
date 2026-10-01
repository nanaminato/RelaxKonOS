package app.relaxkonos.mobile.ui.manage.guardian

import androidx.compose.runtime.*
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import java.net.URI

/** In-memory structured editor; an argument is one value, including empty or multiline values. */
class GuardianDraft(val initial: GuardianDefinition, val creating: Boolean) {
    var name by mutableStateOf(initial.name)
    var executable by mutableStateOf(initial.executablePath)
    val arguments = mutableStateListOf<String>().apply { addAll(initial.arguments) }
    var directory by mutableStateOf(initial.workingDirectory)
    var runAs by mutableStateOf(initial.runAs.orEmpty())
    var enabled by mutableStateOf(initial.enabledOnBoot)
    var stopTimeout by mutableStateOf(initial.stopTimeoutSeconds.toString())
    var attempts by mutableStateOf(initial.maxRestartAttempts.toString())
    var healthType by mutableStateOf(initial.healthCheck?.type.orEmpty())
    var healthTarget by mutableStateOf(initial.healthCheck?.target.orEmpty())
    var interval by mutableStateOf((initial.healthCheck?.intervalSeconds ?: 15).toString())
    var timeout by mutableStateOf((initial.healthCheck?.timeoutSeconds ?: 5).toString())
    var threshold by mutableStateOf((initial.healthCheck?.failureThreshold ?: 3).toString())

    fun definition(platform: String): GuardianDefinition {
        require(name.isNotBlank() && executable.isNotBlank() && directory.isNotBlank() && runAs.isNotBlank())
        require(listOf(name, executable, directory, runAs).all { '\u0000' !in it })
        require(arguments.size <= 128 && arguments.all { it.length <= 16_384 && '\u0000' !in it } && arguments.sumOf { it.length } <= 262_144)
        fun number(text: String, range: IntRange) = text.toInt().also { require(it in range) }
        val health = if (healthType.isEmpty()) null else {
            val kind = healthType.lowercase()
            require(kind in listOf("process", "http", "tcp"))
            val target = healthTarget.trim().ifEmpty { null }
            if (kind == "process") require(target == null)
            else {
                val uri = URI(target ?: error("Health target required"))
                require(uri.isAbsolute && !uri.host.isNullOrBlank() && uri.rawUserInfo == null)
                if (kind == "http") require(uri.scheme.lowercase() in listOf("http", "https"))
                else require(uri.scheme.equals("tcp", true) && uri.port in 1..65535)
            }
            GuardianHealthCheck(healthType, target, number(interval, 1..3600), number(timeout, 1..60), number(threshold, 1..100))
        }
        return initial.copy(name = name.trim(), executablePath = executable.trim(), arguments = arguments.toList(), workingDirectory = directory,
            runAs = runAs.trim(), enabledOnBoot = enabled, stopTimeoutSeconds = number(stopTimeout, 1..300), maxRestartAttempts = number(attempts, 0..100),
            healthCheck = health, runAsIdentity = initial.runAsIdentity.takeIf { guardianSameAccount(platform, initial.runAs, runAs) })
    }
    fun valid(platform: String) = runCatching { definition(platform) }.isSuccess
    fun dirty(platform: String) = runCatching { definition(platform) != initial }.getOrDefault(true)
}

internal fun guardianSameAccount(platform: String, first: String?, second: String?): Boolean = first?.trim()?.equals(second?.trim(),
    ignoreCase = platform.contains("windows", true)) == true

internal fun guardianApprovalRequired(owner: SessionState.Active, target: String) = !guardianSameAccount(owner.serverPlatform, owner.userName, target)
