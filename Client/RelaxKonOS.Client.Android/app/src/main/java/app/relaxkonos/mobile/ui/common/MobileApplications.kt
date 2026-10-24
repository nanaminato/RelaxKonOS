package app.relaxkonos.mobile.ui.common

import androidx.compose.runtime.Composable
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.nav.Routes

/** One capability-filtered directory shared by the home desktop and application search. */
@Composable
internal fun mobileApplications(capabilities: Set<String>, onOpenRoute: (String) -> Unit): List<CatalogApplication> {
    val titles = androidx.compose.ui.platform.LocalContext.current
    fun entry(title: Int, description: Int, icon: Int, open: () -> Unit) =
        CatalogApplication(title.toString(), titles.getString(title), titles.getString(description), icon, open)
    val domains = buildList<CatalogApplication> {
        if (ServerCapabilities.FILES in capabilities) add(entry(R.string.nav_files, R.string.help_files, DesktopIcons.navFiles) { onOpenRoute(Routes.FILES) })
        if (ServerCapabilities.TERMINAL in capabilities) add(entry(R.string.nav_terminal, R.string.help_terminal, DesktopIcons.navTerminal) { onOpenRoute(Routes.TERMINAL) })
        if (capabilities.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS) ||
            capabilities.contains(ServerCapabilities.WEB_SERVER) ||
            capabilities.contains(ServerCapabilities.EVENT_ALERTS) ||
            capabilities.contains(ServerCapabilities.CERTIFICATES) || capabilities.contains(ServerCapabilities.TUNNELS) || capabilities.contains(ServerCapabilities.PROXY) || capabilities.contains(ServerCapabilities.FIREWALL) || capabilities.contains(ServerCapabilities.DOCKER) || capabilities.contains(ServerCapabilities.GUARDIAN) || capabilities.contains(ServerCapabilities.FILE_SERVICES) || capabilities.contains(ServerCapabilities.GATEWAY_PROXY) || capabilities.contains(ServerCapabilities.PROXY_SERVER)) {
            add(entry(R.string.operations_title, R.string.operations_subtitle,
                DesktopIcons.operations, { onOpenRoute(Routes.MANAGE_OPERATIONS) }))
        }
        if (capabilities.contains(ServerCapabilities.DOCKER)) {
            add(entry(R.string.docker_title, R.string.docker_subtitle, R.drawable.ic_app_docker, { onOpenRoute(Routes.MANAGE_DOCKER) }))
        }
        if (capabilities.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS)) {
            add(entry(R.string.deployments_title, R.string.deployments_subtitle,
                DesktopIcons.deployments, { onOpenRoute(Routes.MANAGE_DEPLOYMENTS) }))
        }
        if (capabilities.contains(ServerCapabilities.WEB_SERVER)) {
            add(entry(R.string.websites_title, R.string.websites_subtitle, DesktopIcons.websites, { onOpenRoute(Routes.MANAGE_WEBSITES) }))
        }
        if (capabilities.contains(ServerCapabilities.CERTIFICATES)) {
            add(entry(R.string.certificates_title, R.string.certificates_subtitle, DesktopIcons.certificates, { onOpenRoute(Routes.MANAGE_CERTIFICATES) }))
        }
        if (capabilities.contains(ServerCapabilities.TUNNELS)) {
            add(entry(R.string.tunnels_title, R.string.tunnels_subtitle, DesktopIcons.tunnels, { onOpenRoute(Routes.MANAGE_TUNNELS) }))
        }
        if (capabilities.contains(ServerCapabilities.FILE_SERVICES)) {
            add(entry(R.string.smb_title, R.string.smb_intro, DesktopIcons.smb, { onOpenRoute(Routes.MANAGE_SMB) }))
        }
        if (capabilities.contains(ServerCapabilities.FIREWALL)) {
            add(entry(R.string.firewall_title, R.string.firewall_intro, DesktopIcons.firewall, { onOpenRoute(Routes.MANAGE_FIREWALL) }))
        }
        if (capabilities.contains(ServerCapabilities.GATEWAY_PROXY)) {
            add(entry(R.string.squid_title, R.string.squid_intro, DesktopIcons.proxy, { onOpenRoute(Routes.MANAGE_SQUID) }))
        }
        if (capabilities.contains(ServerCapabilities.PROXY)) {
            add(entry(R.string.mihomo_title, R.string.mihomo_intro, DesktopIcons.proxy, { onOpenRoute(Routes.MANAGE_PROXY) }))
        }
        if (capabilities.contains(ServerCapabilities.PROXY_SERVER)) {
            // A different application from the client-side proxy above: this one is the sing-box
            // server other devices connect to, so it carries the server glyph rather than the node mark.
            add(entry(R.string.proxy_server_title, R.string.proxy_server_intro, DesktopIcons.proxyServer, { onOpenRoute(Routes.MANAGE_PROXY_SERVER) }))
        }
        if (capabilities.contains(ServerCapabilities.GUARDIAN)) {
            add(entry(R.string.guardian_title, R.string.guardian_subtitle, DesktopIcons.guardian, { onOpenRoute(Routes.MANAGE_GUARDIAN) }))
            add(entry(R.string.scripts_title, R.string.scripts_subtitle, DesktopIcons.scripts, { onOpenRoute(Routes.MANAGE_SCRIPTS) }))
        }
        if (capabilities.contains(ServerCapabilities.METRICS) || capabilities.contains(ServerCapabilities.PROCESSES)) {
            add(entry(R.string.workspace_taskmanager, R.string.workspace_taskmanager_note, DesktopIcons.monitor, { onOpenRoute(Routes.MANAGE_MONITOR) }))
        }
    }

    val order = listOf(R.string.nav_files, R.string.docker_title, R.string.websites_title, R.string.deployments_title,
        R.string.tunnels_title, R.string.mihomo_title, R.string.nav_terminal, R.string.workspace_taskmanager,
        R.string.certificates_title, R.string.firewall_title, R.string.guardian_title, R.string.operations_title)
    return domains.sortedBy { order.indexOf(it.id.toInt()).takeIf { index -> index >= 0 } ?: Int.MAX_VALUE }
}
