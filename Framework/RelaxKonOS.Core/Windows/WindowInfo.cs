using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.Core.Windows;

/// <summary>Platform-agnostic description of a managed window. Core stays free of any UI framework.</summary>
public sealed class WindowInfo
{
    public WindowId Id { get; init; }
    public AppId OwnerAppId { get; init; }

    public string Title { get; set; } = string.Empty;
    public string? IconGlyph { get; set; }
    public string? IconPath { get; set; }

    public Rect Bounds { get; set; }
    public Rect RestoreBounds { get; set; }

    public Size MinSize { get; set; } = new(220, 140);

    public WindowState State { get; set; } = WindowState.Normal;
    public bool IsFocused { get; set; }

    public bool CanResize { get; set; } = true;
    public bool CanMinimize { get; set; } = true;
    public bool CanMaximize { get; set; } = true;
}
