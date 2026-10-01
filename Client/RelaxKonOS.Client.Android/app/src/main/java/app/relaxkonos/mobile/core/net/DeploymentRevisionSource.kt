package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

/** Only revision input crosses /deploy; the existing definition and secret references stay on Server. */
data class DeploymentRevisionSource(
    val imageReference: String? = null,
    val archiveReferenceId: String? = null,
    val baseImage: String? = null,
    val runtimeVersion: String? = null,
    val programEntry: String? = null,
    val arguments: List<String> = emptyList(),
    val selfContained: Boolean = false,
) {
    fun validFor(sourceKind: String): Boolean {
        if (arguments.size > 64 || arguments.any { it.length > 4096 || it.any(Char::isISOControl) }) return false
        if (sourceKind == "image") return imageReference?.let(::isPinnedDeploymentImage) == true &&
            archiveReferenceId == null && baseImage == null && runtimeVersion == null && programEntry == null && !selfContained
        if (sourceKind !in setOf("javaJar", "dotNetPublish", "pythonProject") || imageReference != null || archiveReferenceId.isNullOrBlank()) return false
        if (baseImage != null && !isPinnedDeploymentImage(baseImage)) return false
        if (runtimeVersion != null && (runtimeVersion.length > 64 || runtimeVersion.any(Char::isISOControl))) return false
        if (programEntry != null && (programEntry.length > 512 || programEntry.any(Char::isISOControl))) return false
        return (!selfContained || sourceKind == "dotNetPublish") && (sourceKind != "pythonProject" || !programEntry.isNullOrBlank())
    }

    internal fun json(): String = JSONObject().apply {
        imageReference?.let { put("imageReference", it) }
        archiveReferenceId?.let { put("archiveReferenceId", it) }
        baseImage?.let { put("baseImage", it) }
        runtimeVersion?.let { put("runtimeVersion", it) }
        programEntry?.let { put("programEntry", it) }
        if (arguments.isNotEmpty()) put("arguments", JSONArray(arguments))
        put("selfContained", selfContained)
    }.toString()
}

internal fun isPinnedDeploymentImage(value: String): Boolean {
    if (value.isBlank() || value.length > 255 || !value.matches(Regex("[A-Za-z0-9/:._-]+"))) return false
    val last = value.substringAfterLast('/')
    return ':' in last && last.substringBeforeLast(':').isNotBlank() &&
        last.substringAfterLast(':').matches(Regex("[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}")) && !last.substringAfterLast(':').equals("latest", ignoreCase = true)
}
