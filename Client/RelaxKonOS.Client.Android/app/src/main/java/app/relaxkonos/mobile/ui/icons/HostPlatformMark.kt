package app.relaxkonos.mobile.ui.icons

import androidx.annotation.DrawableRes
import androidx.annotation.StringRes
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.HostOperatingSystemKind

/**
 * The mark for one host operating system class.
 *
 * The four `ic_platform_*` vectors are this app's own artwork, not a copy of the desktop set: the
 * desktop never shows what a host runs, so there is nothing to mirror. They are also why the list may
 * only claim what the server said out loud — a system this build has no mark for, and one nobody has
 * asked about yet, both get the generic connection mark instead of wearing somebody else's logo.
 */
@DrawableRes
fun hostPlatformMark(kind: HostOperatingSystemKind?): Int = when (kind) {
    HostOperatingSystemKind.Ubuntu -> R.drawable.ic_platform_ubuntu
    HostOperatingSystemKind.Windows10 -> R.drawable.ic_platform_windows10
    HostOperatingSystemKind.Windows11 -> R.drawable.ic_platform_windows11
    HostOperatingSystemKind.WindowsServer -> R.drawable.ic_platform_windows_server
    HostOperatingSystemKind.Unknown, null -> DesktopIcons.connections
}

/**
 * The name of that mark, for a screen reader, or `null` when there is no mark to describe.
 *
 * The mark is the only place the host's system is written down, so the row would otherwise carry that
 * fact for sighted users alone.
 */
@StringRes
fun hostPlatformMarkLabel(kind: HostOperatingSystemKind?): Int? = when (kind) {
    HostOperatingSystemKind.Ubuntu -> R.string.host_platform_ubuntu
    HostOperatingSystemKind.Windows10 -> R.string.host_platform_windows10
    HostOperatingSystemKind.Windows11 -> R.string.host_platform_windows11
    HostOperatingSystemKind.WindowsServer -> R.string.host_platform_windows_server
    HostOperatingSystemKind.Unknown, null -> null
}
