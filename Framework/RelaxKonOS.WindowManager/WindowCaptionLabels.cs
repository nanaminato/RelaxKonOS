using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.WindowManager;

/// <summary>
/// The three caption actions as words, for automation and assistive technology.
/// </summary>
/// <remarks>
/// The marks themselves are geometry with no text in them - deliberately, because a mark that was text
/// would have to come from a font (see <c>WindowCaptionMarks</c>). That leaves the window manager,
/// which owns the chrome, with nothing to announce; it has no resource table of its own either, so the
/// host pushes its own localized strings in. The English defaults keep a host that never wires them up
/// usable, and they say what the button does rather than reading out a symbol.
/// </remarks>
public sealed partial class WindowCaptionLabels : ObservableObject
{
    /// <summary>The labels every managed window's chrome reads.</summary>
    public static WindowCaptionLabels Current { get; } = new();

    [ObservableProperty] private string _minimize = "Minimize";
    [ObservableProperty] private string _maximize = "Maximize";
    [ObservableProperty] private string _restore = "Restore";
    [ObservableProperty] private string _close = "Close";

    /// <summary>
    /// Replaces all four names at once - what a host does when it starts and again whenever the
    /// display language changes. The four are one call because a partly translated caption would
    /// announce one action in the old language and the next in the new one.
    /// </summary>
    public void Apply(string minimize, string maximize, string restore, string close)
    {
        Minimize = minimize;
        Maximize = maximize;
        Restore = restore;
        Close = close;
    }
}
