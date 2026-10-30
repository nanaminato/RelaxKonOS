package app.relaxkonos.mobile.ui.terminal

import androidx.compose.runtime.MutableState
import androidx.compose.runtime.staticCompositionLocalOf

/**
 * Lets a top-level terminal page hide the shell's bottom navigation while the user reads.
 *
 * The SSH workspace owns its own four-item bar and hides it directly. The Server terminal is a shell
 * destination, so the bar belongs to `ShellScaffold`; this local carries only the flag, and the shell
 * keeps ownership of when a navigation surface exists. `null` means the shell did not offer one.
 */
val LocalTerminalImmersive = staticCompositionLocalOf<MutableState<Boolean>?> { null }
