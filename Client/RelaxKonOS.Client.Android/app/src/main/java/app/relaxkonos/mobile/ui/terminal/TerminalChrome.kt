package app.relaxkonos.mobile.ui.terminal

/**
 * How much management chrome the terminal page spends above the shell viewport.
 *
 * - [Full] keeps the page title, identity, connection line, actions and the session strip.
 * - [Compact] keeps one 48dp bar: back, host, state, expand and the session actions menu.
 * - [Immersive] drops the bar entirely; the workspace navigation hides with it and back exits first.
 *
 * The transcript is the page's purpose, so the phone default is [Compact]; [Full] stays reachable
 * from the menu and is forced on windows wide enough for the two-pane layout.
 */
enum class TerminalChrome { Full, Compact, Immersive }

/**
 * Resolve the chrome actually drawn for the current window.
 *
 * Order matters: a disconnected shell must show its state and retry entry, and a short viewport
 * (IME open, large system font) always wins over the two-pane breakpoint — a tablet with the
 * keyboard up needs the transcript more than it needs the expanded header. Wide windows keep the
 * full header otherwise, because it costs a fixed share of a much taller viewport.
 */
internal fun resolveTerminalChrome(
    preferred: TerminalChrome,
    wide: Boolean,
    shortViewport: Boolean,
    connected: Boolean,
): TerminalChrome {
    if (preferred == TerminalChrome.Immersive && !connected) return TerminalChrome.Compact
    if (shortViewport) return if (preferred == TerminalChrome.Full) TerminalChrome.Compact else preferred
    return if (wide) TerminalChrome.Full else preferred
}
