package app.relaxkonos.mobile.ui.manage.components

import androidx.annotation.DrawableRes
import androidx.annotation.StringRes
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.nav.Routes

/**
 * The components this build can observe. The ids are the server's canonical component ids; each row
 * links to the application that consumes the component, not to an install command.
 *
 * `git` is absent on purpose: Android has no Git workflow, and the mobile support scope keeps it on
 * the desktop. Listing it here would offer a component whose related application does not exist on
 * this client.
 */
internal data class ComponentDefinition(
    val id: String,
    @param:StringRes val title: Int,
    @param:StringRes val description: Int,
    @param:DrawableRes val icon: Int,
    val route: String?,
)

internal object ComponentCatalog {
    val entries = listOf(
        ComponentDefinition("nginx", R.string.components_nginx, R.string.components_nginx_note,
            DesktopIcons.websites, Routes.MANAGE_WEBSITES),
        ComponentDefinition("mihomo", R.string.mihomo_title, R.string.components_mihomo_note,
            DesktopIcons.proxy, Routes.MANAGE_PROXY),
        ComponentDefinition("sing-box", R.string.proxy_server_title, R.string.components_singbox_note,
            DesktopIcons.proxyServer, Routes.MANAGE_PROXY_SERVER),
        ComponentDefinition("docker", R.string.docker_title, R.string.components_docker_note,
            R.drawable.ic_app_docker, Routes.MANAGE_DOCKER),
        ComponentDefinition("samba", R.string.smb_title, R.string.components_samba_note,
            DesktopIcons.smb, Routes.MANAGE_SMB),
        ComponentDefinition("squid", R.string.squid_title, R.string.components_squid_note,
            DesktopIcons.proxy, Routes.MANAGE_SQUID),
        ComponentDefinition("frpc", R.string.tunnels_title, R.string.components_frpc_note,
            DesktopIcons.tunnels, Routes.MANAGE_TUNNELS),
        ComponentDefinition("frps", R.string.tunnels_title, R.string.components_frps_note,
            DesktopIcons.tunnels, Routes.MANAGE_TUNNELS),
    )

    val ids: List<String> = entries.map { it.id }

    /**
     * Components whose changes the component center freezes through the component lifecycle routes.
     * Mihomo and sing-box are excluded: their plan format belongs to the runtime pages.
     */
    val changeableIds: Set<String> = setOf("nginx", "docker", "samba", "squid", "frpc", "frps")

    /**
     * Archive components this screen can install from an explicit trusted version. FRP is a pinned
     * archive pair and the host freezes it through the same component plan route as a package. Other
     * archives (Nginx on Windows) are observed and navigated only: this build refuses their execution.
     */
    val archiveInstallableIds: Set<String> = setOf("frpc", "frps")

    /**
     * Components this build can install from a file reference (a version the operator states plus an
     * archive already on the host). The domain installation service still verifies platform, identity
     * and contents; Nginx supports this path on both of its platforms, FRP as a pinned archive pair.
     */
    val fileInstallableIds: Set<String> = setOf("nginx", "frpc", "frps")

    fun definition(componentId: String): ComponentDefinition? = entries.firstOrNull { it.id == componentId }
}
