using Avalonia.Controls;
using RelaxKonOS.Core;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.WindowManager;

/// <summary>Parameters used to create a managed desktop window.</summary>
public sealed record WindowCreateOptions(
    AppId OwnerAppId,
    string Title,
    Control Content,
    Rect? Bounds = null,
    string? IconGlyph = null,
    bool CanResize = true,
    bool CanMinimize = true,
    bool CanMaximize = true,
    bool IsModalDialog = false,
    WindowInitialPlacement InitialPlacement = WindowInitialPlacement.Explicit,
    string? IconPath = null);
