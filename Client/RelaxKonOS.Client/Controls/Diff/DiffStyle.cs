using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace RelaxKonOS.Client.Controls.Diff;

/// <summary>
/// The brushes a diff view draws with, resolved from the theme tokens of the control that owns it.
/// </summary>
/// <remarks>
/// <para>
/// The palette is rebuilt rather than bound: a row band is an alpha blend of a semantic token, and
/// an alpha blend cannot be expressed as a <c>DynamicResource</c> brush. The instance is mutable so
/// the renderers can hold one reference for their lifetime and read the current values on every
/// frame — a theme or system-style switch is then a repaint instead of a rebuilt visual tree.
/// </para>
/// <para>
/// No colour literal lives here. A token that the active palette does not define yields no brush
/// at all, so a mis-configured host renders a plain diff rather than a plausibly-coloured one: the
/// palette contract is the only way a colour may enter product UI.
/// </para>
/// </remarks>
internal sealed class DiffStyle
{
    /// <summary>How strongly a changed row tints the surface behind it.</summary>
    private const byte ChangedBandAlpha = 0x30;

    /// <summary>Hunk headers are structure, not content, so their band stays fainter.</summary>
    private const byte HunkBandAlpha = 0x24;

    /// <summary>File headers and mode/index lines sit below the hunk headers again.</summary>
    private const byte MetadataBandAlpha = 0x14;

    /// <summary>Opacity of the hairline that separates the gutter from the text.</summary>
    private const byte SeparatorAlpha = 0x38;

    private DiffStyle()
    {
    }

    public IBrush? AddedBand { get; private set; }

    public IBrush? RemovedBand { get; private set; }

    public IBrush? HunkBand { get; private set; }

    public IBrush? MetadataBand { get; private set; }

    public IBrush? AddedInk { get; private set; }

    public IBrush? RemovedInk { get; private set; }

    public IBrush? HunkInk { get; private set; }

    public IBrush? MetadataInk { get; private set; }

    /// <summary>Ink for unchanged line numbers; changed ones reuse the added/removed colour.</summary>
    public IBrush? GutterInk { get; private set; }

    /// <summary>The hairline between the gutter and the text.</summary>
    public IBrush? Separator { get; private set; }

    public static DiffStyle Create(StyledElement host)
    {
        var style = new DiffStyle();
        style.Reload(host);
        return style;
    }

    /// <summary>Re-reads every token. Called on attach and whenever the host's resources change.</summary>
    public void Reload(StyledElement host)
    {
        var added = Token(host, "SuccessColor");
        var removed = Token(host, "DangerColor");
        var accent = Token(host, "AccentColor");
        var muted = Token(host, "TextTertiaryColor");

        AddedBand = Band(added, ChangedBandAlpha);
        RemovedBand = Band(removed, ChangedBandAlpha);
        HunkBand = Band(accent, HunkBandAlpha);
        MetadataBand = Band(muted, MetadataBandAlpha);
        AddedInk = Ink(added);
        RemovedInk = Ink(removed);
        HunkInk = Ink(accent);
        MetadataInk = Ink(muted);
        GutterInk = Ink(muted);
        Separator = Band(muted, SeparatorAlpha);
    }

    /// <summary>The full-width band behind a row, or null when the row keeps the surface colour.</summary>
    public IBrush? BandFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => AddedBand,
        DiffLineKind.Removed => RemovedBand,
        DiffLineKind.HunkHeader => HunkBand,
        DiffLineKind.Metadata => MetadataBand,
        _ => null,
    };

    /// <summary>The text colour for a row, or null to keep the editor's own foreground.</summary>
    public IBrush? InkFor(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => AddedInk,
        DiffLineKind.Removed => RemovedInk,
        DiffLineKind.HunkHeader => HunkInk,
        // A "\ No newline" notice annotates the line above it, so it reads as metadata.
        DiffLineKind.Metadata or DiffLineKind.NoNewline => MetadataInk,
        _ => null,
    };

    /// <summary>
    /// A gutter number is tinted on the side that actually changed, which is what makes "old line 15
    /// became new line 15" readable without tracing colours across the gutter.
    /// </summary>
    public IBrush? NumberInk(DiffLineKind kind, bool oldSide) => (kind, oldSide) switch
    {
        (DiffLineKind.Added, false) => AddedInk,
        (DiffLineKind.Removed, true) => RemovedInk,
        _ => GutterInk,
    };

    private static IBrush? Band(Color? color, byte alpha)
        => color is { } value ? new SolidColorBrush(Color.FromArgb(alpha, value.R, value.G, value.B)) : null;

    private static IBrush? Ink(Color? color) => color is { } value ? new SolidColorBrush(value) : null;

    /// <summary>Reads a palette token from the host's resource chain, falling back to the application's.</summary>
    private static Color? Token(StyledElement host, string key)
    {
        if (host.TryGetResource(key, host.ActualThemeVariant, out var value) && value is Color color)
            return color;
        if (Application.Current is { } application
            && application.TryGetResource(key, host.ActualThemeVariant, out var shared)
            && shared is Color sharedColor)
            return sharedColor;
        return null;
    }
}
