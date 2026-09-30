package app.relaxkonos.mobile.ui.manage.docker

import app.relaxkonos.mobile.core.net.*

internal data class DockerResourceDraft(val action: DockerResourceAction, val name: String = "", val image: String = "",
    val arguments: String = "", val ports: String = "", val environment: String = "", val mounts: String = "", val network: String = "",
    val restart: String = "", val labels: String = "", val cpu: String = "", val memory: String = "", val pids: String = "",
    val logDriver: String = "", val logOptions: String = "", val driver: String = "") {
    fun change(target: String?): DockerResourceChange? = runCatching {
        fun lines(value: String) = value.lines().filter(String::isNotBlank)
        fun optional(value: String) = value.trim().takeIf(String::isNotEmpty)
        when (action) {
            DockerResourceAction.CreateContainer -> {
                val resources = DockerContainerResources(optional(cpu)?.toDouble(), optional(memory)?.toLong(), optional(pids)?.toInt(), optional(logDriver), lines(logOptions))
                val request = DockerContainerCreate(name.trim(), image.trim(), lines(arguments), lines(ports), lines(environment), lines(mounts), optional(network), optional(restart), lines(labels), resources)
                require(DockerResourceValidation.create(request)); DockerResourceChange(action, container = request)
            }
            DockerResourceAction.RenameContainer -> { require(DockerResourceValidation.name(name.trim())); DockerResourceChange(action, target, name.trim()) }
            DockerResourceAction.PullImage -> { require(DockerResourceValidation.image(image.trim())); DockerResourceChange(action, value = image.trim()) }
            DockerResourceAction.CreateNetwork, DockerResourceAction.CreateVolume -> {
                require(DockerResourceValidation.name(name.trim()) && DockerResourceValidation.name(driver.trim()) && DockerResourceValidation.labels(lines(labels)))
                DockerResourceChange(action, value = name.trim(), driver = driver.trim(), labels = lines(labels))
            }
            else -> error("Not an editor action")
        }
    }.getOrNull()
}
