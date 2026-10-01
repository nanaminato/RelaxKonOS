package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.nav.Routes

/** Shipped native workflows only. Capabilities describe availability, never permission grants. */
data class MobileFeature(val title: Int, val description: Int, val route: String, val anyCapabilities: Set<String>) {
    fun available(capabilities: Set<String>) = anyCapabilities.isEmpty() || anyCapabilities.any { it in capabilities }
}
object MobileFeatureCatalog {
    val entries = listOf(
        MobileFeature(R.string.nav_files,R.string.help_files,Routes.FILES,setOf(ServerCapabilities.FILES)),
        MobileFeature(R.string.nav_terminal,R.string.help_terminal,Routes.TERMINAL,setOf(ServerCapabilities.TERMINAL)),
        MobileFeature(R.string.operations_title,R.string.operations_subtitle,Routes.MANAGE_OPERATIONS,setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS,ServerCapabilities.WEB_SERVER,ServerCapabilities.EVENT_ALERTS,ServerCapabilities.CERTIFICATES,ServerCapabilities.TUNNELS,ServerCapabilities.PROXY,ServerCapabilities.FIREWALL)),
        MobileFeature(R.string.deployments_title,R.string.deployments_subtitle,Routes.MANAGE_DEPLOYMENTS,setOf(ServerCapabilities.APPLICATION_DEPLOYMENTS)),
        MobileFeature(R.string.docker_title,R.string.docker_subtitle,Routes.MANAGE_DOCKER,setOf(ServerCapabilities.DOCKER)),
        MobileFeature(R.string.git_title,R.string.git_subtitle,Routes.MANAGE_GIT,setOf(ServerCapabilities.GIT)),
        MobileFeature(R.string.websites_title,R.string.websites_subtitle,Routes.MANAGE_WEBSITES,setOf(ServerCapabilities.WEB_SERVER)),
        MobileFeature(R.string.certificates_title,R.string.certificates_subtitle,Routes.MANAGE_CERTIFICATES,setOf(ServerCapabilities.CERTIFICATES)),
        MobileFeature(R.string.tunnels_title,R.string.tunnels_subtitle,Routes.MANAGE_TUNNELS,setOf(ServerCapabilities.TUNNELS)),
        MobileFeature(R.string.mihomo_title,R.string.mihomo_intro,Routes.MANAGE_PROXY,setOf(ServerCapabilities.PROXY)),
        MobileFeature(R.string.smb_title,R.string.smb_intro,Routes.MANAGE_SMB,setOf(ServerCapabilities.FILE_SERVICES)),
        MobileFeature(R.string.firewall_title,R.string.firewall_intro,Routes.MANAGE_FIREWALL,setOf(ServerCapabilities.FIREWALL)),
        MobileFeature(R.string.guardian_title,R.string.guardian_subtitle,Routes.MANAGE_GUARDIAN,setOf(ServerCapabilities.GUARDIAN)),
        MobileFeature(R.string.scripts_title,R.string.scripts_subtitle,Routes.MANAGE_SCRIPTS,setOf(ServerCapabilities.GUARDIAN)),
        MobileFeature(R.string.manage_monitor_title,R.string.manage_monitor_subtitle,Routes.MANAGE_MONITOR,setOf(ServerCapabilities.METRICS)),
        MobileFeature(R.string.manage_processes_title,R.string.manage_processes_subtitle,Routes.MANAGE_PROCESSES,setOf(ServerCapabilities.PROCESSES)),
        MobileFeature(R.string.host_settings_title,R.string.host_settings_subtitle,Routes.MORE_HOST_SETTINGS,emptySet()),
        MobileFeature(R.string.more_account_security,R.string.more_account_security_subtitle,Routes.MORE_ACCOUNT_SECURITY,emptySet()),
        MobileFeature(R.string.more_connections,R.string.more_connections_subtitle,Routes.MORE_CONNECTIONS,emptySet()),
        MobileFeature(R.string.more_appearance,R.string.more_appearance_subtitle,Routes.MORE_APPEARANCE,emptySet()),
        MobileFeature(R.string.more_diagnostics,R.string.more_diagnostics_subtitle,Routes.MORE_DIAGNOSTICS,emptySet()),
    )
}
