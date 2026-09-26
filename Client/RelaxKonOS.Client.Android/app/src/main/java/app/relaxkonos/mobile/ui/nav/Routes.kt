package app.relaxkonos.mobile.ui.nav

import androidx.annotation.DrawableRes
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ServerCapabilities
import app.relaxkonos.mobile.ui.icons.DesktopIcons

/**
 * Every route the shell can render, defined once. Sub-routes do not carry arguments: a screen that
 * needs a path or a process id receives it from the shared screen state, which keeps the routes
 * comparable and the back stack free of encoded payloads.
 */
object Routes {
    const val CONNECT_LIST = "connect/list"
    const val CONNECT_LOGIN = "connect/login"

    const val HOME = "home"
    const val FILES = "files"
    const val TERMINAL = "terminal"
    const val MANAGE = "manage"
    const val MORE = "more"

    const val FILES_DETAIL = "files/detail"
    const val MANAGE_MONITOR = "manage/monitor"
    const val MANAGE_PROCESSES = "manage/processes"
    const val MANAGE_DEPLOYMENTS = "manage/deployments"
    const val MANAGE_DEPLOYMENT_DETAIL = "manage/deployments/detail"
    const val MORE_ACCOUNT_SECURITY = "more/account-security"
    const val MORE_CONNECTIONS = "more/connections"
    const val MORE_APPEARANCE = "more/appearance"
    const val MORE_DIAGNOSTICS = "more/diagnostics"
    const val MORE_ABOUT = "more/about"
}

/**
 * The five fixed top-level destinations of `RelaxKonOS.Mobile.Design.md` §5.1.
 *
 * [implemented] marks what this build actually ships. An entry that is not implemented yet is not
 * rendered at all, matching the rule that the shell never shows an entry without a usable workflow.
 * The terminal is the remaining V1-C work: it needs the SignalR client and a PTY renderer.
 *
 * [iconRes] addresses the desktop client's app icons, so the bottom bar carries the same four marks
 * the desktop dock does (`ui/icons/DesktopIcons.kt`).
 */
enum class TopDestination(
    val route: String,
    val labelRes: Int,
    @param:DrawableRes val iconRes: Int,
    val implemented: Boolean = true,
    val requiredCapability: String? = null,
) {
    Home(Routes.HOME, R.string.nav_home, DesktopIcons.navHome),
    Files(Routes.FILES, R.string.nav_files, DesktopIcons.navFiles, requiredCapability = ServerCapabilities.FILES),
    Terminal(Routes.TERMINAL, R.string.nav_terminal, DesktopIcons.navTerminal, implemented = false),
    Manage(Routes.MANAGE, R.string.nav_manage, DesktopIcons.navManage),
    More(Routes.MORE, R.string.nav_more, DesktopIcons.navMore);

    companion object {
        /**
         * The destinations this build can actually render for a server advertising [capabilities].
         *
         * An unimplemented destination and one whose capability is absent are both omitted, because
         * the design forbids an entry without a usable workflow (`RelaxKonOS.Mobile.V1.Design.md` §8).
         */
        fun visible(capabilities: Set<String>): List<TopDestination> = entries.filter { destination ->
            destination.implemented && (destination.requiredCapability == null || capabilities.contains(destination.requiredCapability))
        }
    }
}
