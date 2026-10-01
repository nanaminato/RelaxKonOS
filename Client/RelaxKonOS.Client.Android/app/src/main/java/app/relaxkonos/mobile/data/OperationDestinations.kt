package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.InstallationService
import app.relaxkonos.mobile.core.net.OperationalAlert
import app.relaxkonos.mobile.core.net.ServerCapabilities

enum class OperationDestination { Deployment, Website, Compose, GitBuild, GitWorkspace, Script, WebServer, Certificate, Proxy, Tunnels, Docker, Guardian, Firewall, Smb, DockerControl }
data class OperationTarget(val destination: OperationDestination, val id: String? = null)

/** Only implemented native destinations; server-provided URLs and commands never become navigation. */
object OperationDestinations {
    fun capability(domain: OperationDomain, resourceId: String): String? = when (domain) {
        OperationDomain.Deployment -> ServerCapabilities.APPLICATION_DEPLOYMENTS
        OperationDomain.Website, OperationDomain.WebServer -> ServerCapabilities.WEB_SERVER
        OperationDomain.Compose -> ServerCapabilities.DOCKER
        OperationDomain.GitBuild -> ServerCapabilities.GIT
        OperationDomain.Script -> ServerCapabilities.GUARDIAN
        OperationDomain.Backup -> ServerCapabilities.BACKUP_RECOVERY
        OperationDomain.Certificate -> ServerCapabilities.CERTIFICATES
        OperationDomain.Proxy -> ServerCapabilities.PROXY
        OperationDomain.Installation -> InstallationService.entries.firstOrNull { it.name == resourceId }?.capability
    }

    fun operation(owner: SessionState.Active, reference: OperationReference): OperationTarget? {
        if (reference.serviceId != owner.serviceId || reference.account != owner.userName) return null
        if (capability(reference.domain, reference.resourceId) !in owner.capabilities) return null
        val target = when (reference.domain) {
            OperationDomain.Deployment, OperationDomain.Backup -> OperationTarget(OperationDestination.Deployment, reference.resourceId)
            OperationDomain.Website -> OperationTarget(OperationDestination.Website, reference.resourceId)
            OperationDomain.Compose -> OperationTarget(OperationDestination.Compose, reference.resourceId)
            OperationDomain.GitBuild -> OperationTarget(OperationDestination.GitBuild, reference.operationId)
            OperationDomain.Script -> OperationTarget(OperationDestination.Script, reference.operationId)
            OperationDomain.WebServer -> OperationTarget(OperationDestination.WebServer)
            OperationDomain.Certificate -> OperationTarget(OperationDestination.Certificate, reference.operationId)
            OperationDomain.Proxy -> OperationTarget(OperationDestination.Proxy, reference.operationId)
            OperationDomain.Installation -> installation(owner, reference.resourceId)
        }
        return target?.takeIf { it.destination != OperationDestination.Deployment || ServerCapabilities.APPLICATION_DEPLOYMENTS in owner.capabilities }
    }

    fun installation(owner: SessionState.Active, service: String): OperationTarget? {
        val kind = InstallationService.entries.firstOrNull { it.name == service } ?: return null
        if (kind.capability !in owner.capabilities) return null
        return when (kind) {
            InstallationService.Git -> OperationTarget(OperationDestination.GitWorkspace)
            InstallationService.Docker -> OperationTarget(OperationDestination.DockerControl)
            InstallationService.Smb -> OperationTarget(OperationDestination.Smb)
            InstallationService.Nginx -> OperationTarget(OperationDestination.WebServer)
            InstallationService.Frp -> OperationTarget(OperationDestination.Tunnels)
            InstallationService.Mihomo -> OperationTarget(OperationDestination.Proxy)
        }
    }

    fun alert(owner: SessionState.Active, alert: OperationalAlert): OperationTarget? {
        val pair = when (alert.targetKind) {
            "applicationDeployment", "applicationDeploymentOperation" -> ServerCapabilities.APPLICATION_DEPLOYMENTS to
                alert.targetResourceId?.takeIf(String::isNotBlank)?.let { OperationTarget(OperationDestination.Deployment, it) }
            "certificate" -> ServerCapabilities.CERTIFICATES to OperationTarget(OperationDestination.Certificate, alert.targetOperationId)
            "guardianOverview", "guardianWorkload" -> ServerCapabilities.GUARDIAN to OperationTarget(OperationDestination.Guardian)
            "dockerOverview" -> ServerCapabilities.DOCKER to OperationTarget(OperationDestination.Docker)
            "tunnelDefinition" -> ServerCapabilities.TUNNELS to OperationTarget(OperationDestination.Tunnels)
            else -> return null
        }
        return pair.second.takeIf { pair.first in owner.capabilities }
    }
}
