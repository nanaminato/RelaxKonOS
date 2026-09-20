using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments;

/// <summary>
/// The single table that decides how an observed state is presented. The header badge, the list dot,
/// the readiness row and the status animation all read it, so one state can never be green in one
/// place and grey in another.
///
/// Presentation never rests on colour alone: every family also supplies a distinct glyph and the
/// caller supplies the state's own text. An operator with a colour-vision difference, a monochrome
/// display, or a low-quality projector reads the shape and the word instead.
/// </summary>
internal static class DeploymentStatusVisual
{
    /// <summary>
    /// The colour families the workspace palette can actually express. Deliberately small: a state
    /// picks a family rather than an arbitrary colour, so a palette swap restyles every state at
    /// once and no page can introduce a colour the theme has no token for.
    /// </summary>
    internal enum Family { Neutral, Info, Success, Warning, Danger }

    internal static Family FamilyOf(ApplicationActualState state) => state switch
    {
        ApplicationActualState.Running => Family.Success,
        // Rising and falling are both "in flight", so they share a family and a glyph; the state's
        // own text is what tells them apart.
        ApplicationActualState.Starting or ApplicationActualState.Stopping => Family.Info,
        ApplicationActualState.Failed or ApplicationActualState.Missing => Family.Danger,
        // Not yet reconciled against a real container. Reported as unknown rather than assumed
        // healthy, which is why it is a warning and not a success.
        ApplicationActualState.Unknown => Family.Warning,
        _ => Family.Neutral,
    };

    /// <summary>
    /// A state the workload is moving through, as opposed to one it is resting in. The caller
    /// animates the glyph for these and leaves the resting glyphs still, so motion itself means
    /// "in progress" instead of being decoration on every row.
    /// </summary>
    internal static bool IsTransitioning(ApplicationActualState state) =>
        state is ApplicationActualState.Starting or ApplicationActualState.Stopping;

    /// <summary>
    /// A shape that repeats what the colour says. Kept distinct per state so the symbol set alone is
    /// enough to scan a list.
    /// </summary>
    internal static string GlyphOf(ApplicationActualState state) => state switch
    {
        ApplicationActualState.Running => "\u25CF",   // ● filled
        ApplicationActualState.Stopped => "\u25CB",   // ○ hollow
        ApplicationActualState.Failed => "\u26A0",    // ⚠ warning triangle
        ApplicationActualState.Missing => "\u2298",   // ⊘ crossed out
        ApplicationActualState.Unknown => "?",
        _ => "\u25D4",                                // ◔ in flight
    };

    /// <summary>
    /// The coloured element for a family, used for the badge border, its dot and its text.
    ///
    /// The badge deliberately keeps a sunken background and draws only its outline in this colour:
    /// a saturated fill would have to carry body text, and the palette has muted tokens for success,
    /// warning and accent but none for danger, so a filled variant could not be uniform across all
    /// five families.
    /// </summary>
    internal static string AccentKey(Family family) => family switch
    {
        Family.Success => "SuccessBrush",
        Family.Info => "InfoBrush",
        Family.Warning => "WarningBrush",
        Family.Danger => "DangerBrush",
        _ => "BorderDefaultBrush",
    };

    /// <summary>
    /// Text colour on top of <see cref="AccentKey"/>. The neutral family has no meaning to colour,
    /// so it falls back to the ordinary secondary text role rather than a borrowed border colour.
    /// </summary>
    internal static string ForegroundKey(Family family) =>
        family == Family.Neutral ? "TextSecondaryBrush" : AccentKey(family);
}
