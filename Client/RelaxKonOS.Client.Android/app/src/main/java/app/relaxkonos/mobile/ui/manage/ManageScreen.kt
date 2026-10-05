package app.relaxkonos.mobile.ui.manage

import androidx.compose.runtime.setValue
import androidx.compose.runtime.getValue
import app.relaxkonos.mobile.ui.common.*
import androidx.annotation.DrawableRes
import androidx.annotation.StringRes
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.common.EmptyState
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * A manageable domain and the glyph that identifies it.
 *
 * The glyph is a resource id from the desktop icon set (`ui/icons/DesktopIcons.kt`), not a Material
 * `ImageVector`: the phone shows the same marks the desktop shows for the same two domains.
 */
private data class ManageDomain(
    @param:StringRes val titleRes: Int,
    @param:StringRes val subtitleRes: Int,
    @param:DrawableRes val iconRes: Int,
    val open: () -> Unit,
)

/**
 * Manage.
 *
 * Only domains with a working mobile workflow are listed. The design forbids adding an entry just
 * because the desktop has one (`Shell.Design.md` §8). Each entry opens its implemented domain workflow, including Nginx sites and certificate management.
 *
 * A local search and adaptive card grid keep the available applications reachable at every width.
 */
@Composable
fun ManageScreen(
    onOpenMonitor: () -> Unit,
    onOpenDeployments: () -> Unit,
    onOpenDocker: () -> Unit,
    onOpenGit: () -> Unit,
    onOpenWebsites: () -> Unit,
    onOpenCertificates: () -> Unit,
    onOpenTunnels: () -> Unit,
    onOpenProxy: () -> Unit,
    onOpenSmb: () -> Unit,
    onOpenFirewall: () -> Unit,
    onOpenGuardian: () -> Unit,
    onOpenScripts: () -> Unit,
    onOpenOperations: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val container = app.relaxkonos.mobile.ui.common.appContainer()

    val domains = buildList {
        if (container.capabilities.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS) ||
            container.capabilities.contains(ServerCapabilities.WEB_SERVER) ||
            container.capabilities.contains(ServerCapabilities.EVENT_ALERTS) ||
            container.capabilities.contains(ServerCapabilities.CERTIFICATES) || container.capabilities.contains(ServerCapabilities.TUNNELS) || container.capabilities.contains(ServerCapabilities.PROXY) || container.capabilities.contains(ServerCapabilities.FIREWALL)) {
            add(ManageDomain(R.string.operations_title, R.string.operations_subtitle,
                DesktopIcons.operations, onOpenOperations))
        }
        if (container.capabilities.contains(ServerCapabilities.DOCKER)) {
            add(ManageDomain(R.string.docker_title, R.string.docker_subtitle, R.drawable.ic_app_docker, onOpenDocker))
        }
        if (container.capabilities.contains(ServerCapabilities.GIT)) {
            add(ManageDomain(R.string.git_title, R.string.git_subtitle, R.drawable.ic_sys_file_git_config, onOpenGit))
        }
        if (container.capabilities.contains(ServerCapabilities.APPLICATION_DEPLOYMENTS)) {
            add(ManageDomain(R.string.deployments_title, R.string.deployments_subtitle,
                DesktopIcons.deployments, onOpenDeployments))
        }
        if (container.capabilities.contains(ServerCapabilities.WEB_SERVER)) {
            add(ManageDomain(R.string.websites_title, R.string.websites_subtitle, DesktopIcons.websites, onOpenWebsites))
        }
        if (container.capabilities.contains(ServerCapabilities.CERTIFICATES)) {
            add(ManageDomain(R.string.certificates_title, R.string.certificates_subtitle, DesktopIcons.certificates, onOpenCertificates))
        }
        if (container.capabilities.contains(ServerCapabilities.TUNNELS)) {
            add(ManageDomain(R.string.tunnels_title, R.string.tunnels_subtitle, DesktopIcons.tunnels, onOpenTunnels))
        }
        if (container.capabilities.contains(ServerCapabilities.FILE_SERVICES)) {
            add(ManageDomain(R.string.smb_title, R.string.smb_intro, DesktopIcons.smb, onOpenSmb))
        }
        if (container.capabilities.contains(ServerCapabilities.FIREWALL)) {
            add(ManageDomain(R.string.firewall_title, R.string.firewall_intro, DesktopIcons.firewall, onOpenFirewall))
        }
        if (container.capabilities.contains(ServerCapabilities.PROXY)) {
            add(ManageDomain(R.string.mihomo_title, R.string.mihomo_intro, DesktopIcons.proxy, onOpenProxy))
        }
        if (container.capabilities.contains(ServerCapabilities.GUARDIAN)) {
            add(ManageDomain(R.string.guardian_title, R.string.guardian_subtitle, DesktopIcons.guardian, onOpenGuardian))
            add(ManageDomain(R.string.scripts_title, R.string.scripts_subtitle, DesktopIcons.scripts, onOpenScripts))
        }
        if (container.capabilities.contains(ServerCapabilities.METRICS) || container.capabilities.contains(ServerCapabilities.PROCESSES)) {
            add(ManageDomain(R.string.workspace_taskmanager, R.string.workspace_taskmanager_note, DesktopIcons.monitor, onOpenMonitor))
        }
    }

    Column(
        modifier = modifier.fillMaxSize().padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(title = stringResource(R.string.manage_title))

        if (domains.isEmpty()) {
            EmptyState(
                text = stringResource(R.string.error_capability_missing),
                icon = DesktopIcons.notice,
            )
        } else {
            ApplicationCatalog(
                applications = domains.map { domain ->
                    CatalogApplication(
                        id = domain.titleRes.toString(),
                        title = stringResource(domain.titleRes),
                        description = stringResource(domain.subtitleRes),
                        icon = domain.iconRes, open = domain.open,
                    )
                },
                modifier = Modifier.weight(1f),
            )
        }
    }
}
