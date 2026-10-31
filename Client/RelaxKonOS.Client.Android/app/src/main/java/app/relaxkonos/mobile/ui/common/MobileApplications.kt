package app.relaxkonos.mobile.ui.common

import androidx.compose.runtime.Composable
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.nav.Routes

/** Current capabilities plus observed module providers, shared by home and application search. */
@Composable
internal fun mobileApplications(capabilities: Set<String>, privilegedOperations: Boolean,
    modules: List<app.relaxkonos.mobile.core.net.ServerModuleFact> = emptyList(), onOpenModule: ((String) -> Unit)? = null,
    onOpenRoute: (String) -> Unit): List<CatalogApplication> {
    val discoverable = capabilities + if (onOpenModule == null) emptySet() else modules.flatMap { it.capabilities }.toSet()
    val openRoute: (String) -> Unit = { route ->
        val moduleId = missingModuleForRoute(route, capabilities, modules)
        if (moduleId != null && onOpenModule != null) onOpenModule(moduleId) else onOpenRoute(route)
    }
    val titles = androidx.compose.ui.platform.LocalContext.current
    fun entry(title: Int, description: Int, icon: Int, open: () -> Unit) =
        CatalogApplication(title.toString(), titles.getString(title), titles.getString(description), icon, open)
    val domains = buildList<CatalogApplication> {
        if (ServerCapabilities.FILES in discoverable) add(entry(R.string.nav_files, R.string.help_files, DesktopIcons.navFiles) { openRoute(Routes.FILES) })
        if (ServerCapabilities.TERMINAL in discoverable) add(entry(R.string.nav_terminal, R.string.help_terminal, DesktopIcons.navTerminal) { openRoute(Routes.TERMINAL) })
        if (discoverable.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS) ||
            discoverable.contains(ServerCapabilities.WEB_SERVER) ||
            discoverable.contains(ServerCapabilities.EVENT_ALERTS) ||
            discoverable.contains(ServerCapabilities.CERTIFICATES) || discoverable.contains(ServerCapabilities.TUNNELS) || discoverable.contains(ServerCapabilities.PROXY) || discoverable.contains(ServerCapabilities.FIREWALL) || discoverable.contains(ServerCapabilities.DOCKER) || discoverable.contains(ServerCapabilities.GUARDIAN) || discoverable.contains(ServerCapabilities.FILE_SERVICES) || discoverable.contains(ServerCapabilities.GATEWAY_PROXY) || discoverable.contains(ServerCapabilities.PROXY_SERVER)) {
            add(entry(R.string.operations_title, R.string.operations_subtitle,
                DesktopIcons.operations, { openRoute(Routes.MANAGE_OPERATIONS) }))
        }
        // Host software needs privileged operations; Core module management remains discoverable
        // independently when the authenticated server supplies its module directory.
        if (privilegedOperations || modules.isNotEmpty()) {
            add(entry(R.string.components_title, R.string.components_subtitle,
                DesktopIcons.applications, { openRoute(Routes.MANAGE_COMPONENTS) }))
        }
        if (discoverable.contains(ServerCapabilities.DOCKER)) {
            add(entry(R.string.docker_title, R.string.docker_subtitle, R.drawable.ic_app_docker, { openRoute(Routes.MANAGE_DOCKER) }))
        }
        if (discoverable.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS)) {
            add(entry(R.string.deployments_title, R.string.deployments_subtitle,
                DesktopIcons.deployments, { openRoute(Routes.MANAGE_DEPLOYMENTS) }))
        }
        if (discoverable.contains(ServerCapabilities.WEB_SERVER)) {
            add(entry(R.string.websites_title, R.string.websites_subtitle, DesktopIcons.websites, { openRoute(Routes.MANAGE_WEBSITES) }))
        }
        if (discoverable.contains(ServerCapabilities.CERTIFICATES)) {
            add(entry(R.string.certificates_title, R.string.certificates_subtitle, DesktopIcons.certificates, { openRoute(Routes.MANAGE_CERTIFICATES) }))
        }
        if (discoverable.contains(ServerCapabilities.TUNNELS)) {
            add(entry(R.string.tunnels_title, R.string.tunnels_subtitle, DesktopIcons.tunnels, { openRoute(Routes.MANAGE_TUNNELS) }))
        }
        if (discoverable.contains(ServerCapabilities.FILE_SERVICES)) {
            add(entry(R.string.smb_title, R.string.smb_intro, DesktopIcons.smb, { openRoute(Routes.MANAGE_SMB) }))
        }
        if (discoverable.contains(ServerCapabilities.FIREWALL)) {
            add(entry(R.string.firewall_title, R.string.firewall_intro, DesktopIcons.firewall, { openRoute(Routes.MANAGE_FIREWALL) }))
        }
        if (ServerCapabilities.TAILSCALE in discoverable) {
            add(entry(R.string.tailscale_title, R.string.tailscale_intro, DesktopIcons.tunnels) { openRoute(Routes.MANAGE_TAILSCALE) })
        }
        if (discoverable.contains(ServerCapabilities.GATEWAY_PROXY)) {
            add(entry(R.string.squid_title, R.string.squid_intro, DesktopIcons.proxy, { openRoute(Routes.MANAGE_SQUID) }))
        }
        if (discoverable.contains(ServerCapabilities.PROXY)) {
            add(entry(R.string.mihomo_title, R.string.mihomo_intro, DesktopIcons.proxy, { openRoute(Routes.MANAGE_PROXY) }))
        }
        if (discoverable.contains(ServerCapabilities.PROXY_SERVER)) {
            // A different application from the client-side proxy above: this one is the sing-box
            // server other devices connect to, so it carries the server glyph rather than the node mark.
            add(entry(R.string.proxy_server_title, R.string.proxy_server_intro, DesktopIcons.proxyServer, { openRoute(Routes.MANAGE_PROXY_SERVER) }))
        }
        if (discoverable.contains(ServerCapabilities.GUARDIAN)) {
            add(entry(R.string.guardian_title, R.string.guardian_subtitle, DesktopIcons.guardian, { openRoute(Routes.MANAGE_GUARDIAN) }))
            add(entry(R.string.scripts_title, R.string.scripts_subtitle, DesktopIcons.scripts, { openRoute(Routes.MANAGE_SCRIPTS) }))
        }
        if (discoverable.contains(ServerCapabilities.METRICS) || discoverable.contains(ServerCapabilities.PROCESSES)) {
            add(entry(R.string.workspace_taskmanager, R.string.workspace_taskmanager_note, DesktopIcons.monitor, { openRoute(Routes.MANAGE_MONITOR) }))
        }
    }

    val order = listOf(R.string.nav_files, R.string.docker_title, R.string.websites_title, R.string.deployments_title,
        R.string.tunnels_title, R.string.mihomo_title, R.string.nav_terminal, R.string.workspace_taskmanager,
        R.string.certificates_title, R.string.firewall_title, R.string.guardian_title, R.string.operations_title,
        R.string.components_title)
    return domains.sortedBy { order.indexOf(it.id.toInt()).takeIf { index -> index >= 0 } ?: Int.MAX_VALUE }
}
